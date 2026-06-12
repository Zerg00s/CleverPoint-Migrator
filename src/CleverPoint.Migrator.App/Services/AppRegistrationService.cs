using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace CleverPoint.Migrator.App.Services;

/// <summary>
/// In-app port of Setup-MigrationApp.ps1: signs a Global Admin in through
/// the browser (OAuth code + PKCE on a localhost listener via the well-known
/// Microsoft Graph public client), then provisions an Entra app registration
/// with SharePoint application permissions, admin consent, a client secret
/// and a self-signed certificate. Idempotent and re-runnable like the script.
/// </summary>
public class AppRegistrationService
{
    private const string SignInClientId = "14d82eec-204b-4c2f-b7e8-296a70dab67e";   // Microsoft Graph Command Line Tools
    private const string Authority = "https://login.microsoftonline.com/organizations";
    private const string GraphBase = "https://graph.microsoft.com/v1.0";
    private const string SpoResourceAppId = "00000003-0000-0ff1-ce00-000000000000";
    private static readonly string[] SpoRoles = { "Sites.FullControl.All", "TermStore.ReadWrite.All", "User.ReadWrite.All" };

    private readonly HttpClient _http = new();
    private string? _token;

    public event Action<string>? OnProgress;

    /// <summary>Step 1: browser sign-in; returns the admin UPN.</summary>
    public async Task<string> SignInAsync()
    {
        var rng = RandomNumberGenerator.Create();
        var verifierBytes = new byte[32];
        rng.GetBytes(verifierBytes);
        var verifier = Base64Url(verifierBytes);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        HttpListener? listener = null;
        string? redirect = null;
        foreach (var port in new[] { 53682, 53683, 53684, 53685 })
        {
            try
            {
                var candidate = new HttpListener();
                candidate.Prefixes.Add($"http://localhost:{port}/");
                candidate.Start();
                listener = candidate;
                redirect = $"http://localhost:{port}/";
                break;
            }
            catch (HttpListenerException) { }
        }
        if (listener == null) throw new InvalidOperationException("No localhost port available for the sign-in redirect (53682-53685).");

        const string scopes = "https://graph.microsoft.com/Application.ReadWrite.All https://graph.microsoft.com/AppRoleAssignment.ReadWrite.All https://graph.microsoft.com/Directory.Read.All openid profile offline_access";
        var authUrl = $"{Authority}/oauth2/v2.0/authorize?client_id={SignInClientId}&response_type=code&response_mode=query" +
            $"&redirect_uri={Uri.EscapeDataString(redirect!)}&scope={Uri.EscapeDataString(scopes)}" +
            $"&code_challenge={challenge}&code_challenge_method=S256&prompt=select_account";
        OnProgress?.Invoke("opening the browser; sign in with your Global Admin account");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(authUrl) { UseShellExecute = true });

        var contextTask = listener.GetContextAsync();
        if (await Task.WhenAny(contextTask, Task.Delay(TimeSpan.FromMinutes(5))) != contextTask)
        {
            listener.Stop();
            throw new TimeoutException("Sign-in timed out after 5 minutes.");
        }
        var context = contextTask.Result;
        var code = context.Request.QueryString["code"];
        var error = context.Request.QueryString["error_description"] ?? context.Request.QueryString["error"];
        var html = Encoding.UTF8.GetBytes("<html><body style='font-family:Segoe UI;margin:40px'><h2>Sign-in complete.</h2><p>Return to CleverPoint Migrator.</p></body></html>");
        context.Response.ContentType = "text/html";
        await context.Response.OutputStream.WriteAsync(html);
        context.Response.Close();
        listener.Stop();
        if (code == null) throw new InvalidOperationException($"Sign-in failed: {error ?? "no authorization code returned"}");

        var tokenResponse = await _http.PostAsync($"{Authority}/oauth2/v2.0/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = SignInClientId,
            ["code"] = code,
            ["redirect_uri"] = redirect!,
            ["code_verifier"] = verifier,
            ["scope"] = scopes,
        }));
        var body = await tokenResponse.Content.ReadAsStringAsync();
        if (!tokenResponse.IsSuccessStatusCode) throw new InvalidOperationException($"Token request failed: {body}");
        using var doc = JsonDocument.Parse(body);
        _token = doc.RootElement.GetProperty("access_token").GetString();

        var claims = DecodeJwtPayload(_token!);
        return claims.TryGetProperty("upn", out var upn) ? upn.GetString() ?? "(admin)" : "(admin)";
    }

    public record ProvisionResult(string TenantId, string TenantName, string SpoUrl, string AppId,
        string Secret, string SecretExpires, string CertThumbprint, string PfxPath, string PfxPassword);

    /// <summary>Step 2: create/configure the app, consent, secret + certificate. Saves the PFX locally.</summary>
    public async Task<ProvisionResult> ProvisionAsync(string appName, string outputFolder)
    {
        // Tenant info + SPO URL from the onmicrosoft domain.
        using var org = await GraphAsync(HttpMethod.Get, "/organization?$select=id,displayName,verifiedDomains");
        var orgElement = org.RootElement.GetProperty("value")[0];
        var tenantId = orgElement.GetProperty("id").GetString()!;
        var tenantName = orgElement.GetProperty("displayName").GetString() ?? tenantId;
        var prefix = orgElement.GetProperty("verifiedDomains").EnumerateArray()
            .Select(d => d.GetProperty("name").GetString() ?? "")
            .First(n => n.EndsWith(".onmicrosoft.com") && !n.Contains(".mail.")).Split('.')[0];
        var spoUrl = $"https://{prefix}.sharepoint.com";
        OnProgress?.Invoke($"tenant: {tenantName}");

        // Find or create the app registration.
        using var existing = await GraphAsync(HttpMethod.Get,
            $"/applications?$filter=displayName eq '{appName.Replace("'", "''")}'&$select=id,appId");
        string objectId, appId;
        if (existing.RootElement.GetProperty("value").GetArrayLength() > 0)
        {
            var app = existing.RootElement.GetProperty("value")[0];
            objectId = app.GetProperty("id").GetString()!;
            appId = app.GetProperty("appId").GetString()!;
            OnProgress?.Invoke($"reusing app '{appName}' ({appId})");
        }
        else
        {
            using var created = await GraphAsync(HttpMethod.Post, "/applications",
                new { displayName = appName, signInAudience = "AzureADMyOrg" });
            objectId = created.RootElement.GetProperty("id").GetString()!;
            appId = created.RootElement.GetProperty("appId").GetString()!;
            OnProgress?.Invoke($"created app '{appName}' ({appId})");
        }

        // SPO application roles, resolved dynamically by value.
        using var spoSp = await GraphAsync(HttpMethod.Get,
            $"/servicePrincipals?$filter=appId eq '{SpoResourceAppId}'&$select=id,appRoles");
        var spoSpElement = spoSp.RootElement.GetProperty("value")[0];
        var spoSpId = spoSpElement.GetProperty("id").GetString()!;
        var roleIds = spoSpElement.GetProperty("appRoles").EnumerateArray()
            .Where(r => SpoRoles.Contains(r.GetProperty("value").GetString()))
            .Select(r => (Value: r.GetProperty("value").GetString()!, Id: r.GetProperty("id").GetString()!))
            .ToList();

        await GraphAsync(HttpMethod.Patch, $"/applications/{objectId}", new
        {
            requiredResourceAccess = new[]
            {
                new
                {
                    resourceAppId = SpoResourceAppId,
                    resourceAccess = roleIds.Select(r => new { id = r.Id, type = "Role" }).ToArray(),
                },
            },
        });
        OnProgress?.Invoke("API permissions configured");

        // Service principal + admin consent (idempotent).
        using var spLookup = await GraphAsync(HttpMethod.Get, $"/servicePrincipals?$filter=appId eq '{appId}'&$select=id");
        string spId;
        if (spLookup.RootElement.GetProperty("value").GetArrayLength() > 0)
        {
            spId = spLookup.RootElement.GetProperty("value")[0].GetProperty("id").GetString()!;
        }
        else
        {
            using var spCreated = await GraphAsync(HttpMethod.Post, "/servicePrincipals", new { appId });
            spId = spCreated.RootElement.GetProperty("id").GetString()!;
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
        using var assignments = await GraphAsync(HttpMethod.Get, $"/servicePrincipals/{spId}/appRoleAssignments");
        var granted = assignments.RootElement.GetProperty("value").EnumerateArray()
            .Select(a => a.GetProperty("appRoleId").GetString()).ToHashSet();
        foreach (var (value, id) in roleIds.Where(r => !granted.Contains(r.Id)))
        {
            await GraphAsync(HttpMethod.Post, $"/servicePrincipals/{spId}/appRoleAssignments",
                new { principalId = spId, resourceId = spoSpId, appRoleId = id });
            OnProgress?.Invoke($"admin consent granted: {value}");
        }

        // Client secret (24 months).
        using var secretDoc = await GraphAsync(HttpMethod.Post, $"/applications/{objectId}/addPassword", new
        {
            passwordCredential = new
            {
                displayName = "CleverPoint Migrator (app wizard)",
                endDateTime = DateTime.UtcNow.AddMonths(24).ToString("yyyy-MM-ddTHH:mm:ssZ"),
            },
        });
        var secret = secretDoc.RootElement.GetProperty("secretText").GetString()!;
        var secretExpires = secretDoc.RootElement.GetProperty("endDateTime").GetString()!;

        // Self-signed certificate (2 years) + registration on the app.
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={appName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow.AddYears(2));
        var pfxPassword = Base64Url(RandomNumberGenerator.GetBytes(18));
        Directory.CreateDirectory(outputFolder);
        var pfxPath = Path.Combine(outputFolder, $"{Sanitize(appName)}.pfx");
        await File.WriteAllBytesAsync(pfxPath, cert.Export(X509ContentType.Pfx, pfxPassword));

        // Merge into keyCredentials (skip when the same thumbprint is present).
        using var appDoc = await GraphAsync(HttpMethod.Get, $"/applications/{objectId}?$select=keyCredentials");
        var newKeyId = Convert.ToBase64String(cert.GetCertHash());
        var keys = new List<object>();
        foreach (var k in appDoc.RootElement.GetProperty("keyCredentials").EnumerateArray())
        {
            if (k.GetProperty("customKeyIdentifier").GetString() == newKeyId) continue;
            if (k.TryGetProperty("key", out var keyValue) && keyValue.ValueKind == JsonValueKind.String)
                keys.Add(new
                {
                    type = k.GetProperty("type").GetString(),
                    usage = k.GetProperty("usage").GetString(),
                    key = keyValue.GetString(),
                    displayName = k.GetProperty("displayName").GetString(),
                });
        }
        keys.Add(new
        {
            type = "AsymmetricX509Cert",
            usage = "Verify",
            key = Convert.ToBase64String(cert.RawData),
            displayName = "CleverPoint Migrator (app wizard)",
        });
        await GraphAsync(HttpMethod.Patch, $"/applications/{objectId}", new { keyCredentials = keys });
        OnProgress?.Invoke($"certificate registered ({cert.Thumbprint})");

        return new ProvisionResult(tenantId, tenantName, spoUrl, appId, secret, secretExpires,
            cert.Thumbprint, pfxPath, pfxPassword);
    }

    private async Task<JsonDocument> GraphAsync(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path.StartsWith("http") ? path : GraphBase + path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
        if (body != null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var response = await _http.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Graph {method} {path} failed ({(int)response.StatusCode}): {content}");
        return JsonDocument.Parse(content.Length > 0 ? content : "{}");
    }

    private static JsonElement DecodeJwtPayload(string jwt)
    {
        var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        return JsonDocument.Parse(Convert.FromBase64String(payload)).RootElement;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Sanitize(string s) =>
        string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-'));
}

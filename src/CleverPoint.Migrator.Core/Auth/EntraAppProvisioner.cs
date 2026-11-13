using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Identity.Client;

namespace CleverPoint.Migrator.Core.Auth;

/// <summary>The tenant's sign-in app, as found or created by <see cref="EntraAppProvisioner"/>.</summary>
public sealed record SignInAppResult(string TenantId, string AppId, bool Created);

/// <summary>The tenant's unattended (app-only) app and the certificate just issued for this machine.</summary>
public sealed record UnattendedAppResult(string TenantId, string AppId, bool Created,
    string PfxPath, string PfxPassword, string Thumbprint, DateTime CertExpiresUtc);

/// <summary>
/// Registers CleverPoint Migrator in a customer's own Entra tenant, once, and asks an administrator
/// to approve it on Microsoft's consent page.
///
/// The app is found by a fixed unique name, so running the setup again (from this or any other
/// computer) reuses the existing registration instead of creating a second one. Two separate
/// registrations exist on purpose:
///   - the sign-in app: delegated permissions only, a public client with no secret. It can never do
///     more than the person signed in;
///   - the unattended app (opt-in): application permissions and a certificate. It reaches every site
///     in the tenant regardless of who runs the tool, so it is kept apart and can be deleted alone.
///
/// Microsoft Graph is used here because it is the only API that can register an application.
/// SharePoint content is never read or written through Graph.
/// </summary>
public sealed class EntraAppProvisioner
{
    /// <summary>Microsoft Graph Command Line Tools: Microsoft's own public client, present in every tenant.</summary>
    private const string BootstrapClientId = "14d82eec-204b-4c2f-b7e8-296a70dab67e";
    private const string GraphBase = "https://graph.microsoft.com/v1.0";
    private const string SharePointResourceAppId = "00000003-0000-0ff1-ce00-000000000000";

    public const string SignInAppUniqueName = "cleverpoint-migrator";
    public const string SignInAppDisplayName = "CleverPoint Migrator";
    public const string UnattendedAppUniqueName = "cleverpoint-migrator-unattended";
    public const string UnattendedAppDisplayName = "CleverPoint Migrator (unattended)";

    /// <summary>Delegated SharePoint permissions of the sign-in app. FullControl because permissions and list settings are copied.</summary>
    public static readonly string[] DelegatedScopes = { "AllSites.FullControl", "TermStore.ReadWrite.All" };

    /// <summary>Application SharePoint permissions of the unattended app.</summary>
    public static readonly string[] ApplicationRoles = { "Sites.FullControl.All", "TermStore.ReadWrite.All", "User.ReadWrite.All" };

    private readonly HttpClient _http;
    private string? _token;
    private string? _tenantId;

    /// <summary>One line per completed step, for the setup dialog or the console.</summary>
    public event Action<string>? OnProgress;

    /// <summary>How a URL is shown to the administrator. Defaults to the system browser.</summary>
    public Action<string> OpenBrowser { get; set; } = DefaultOpenBrowser;

    public EntraAppProvisioner(HttpClient? http = null) => _http = http ?? new HttpClient();

    /// <summary>The tenant the administrator signed in to.</summary>
    public string TenantId => _tenantId ?? throw new InvalidOperationException("Sign in as an administrator first.");

    /// <summary>
    /// Step 1: an administrator signs in through Microsoft's own setup client, in the tenant that owns
    /// <paramref name="sharePointHost"/>. Only the right to manage app registrations is requested.
    /// Returns the administrator's sign-in name.
    /// </summary>
    public async Task<string> SignInAdminAsync(string sharePointHost, CancellationToken ct = default)
    {
        _tenantId = await TenantDiscovery.GetTenantIdAsync(sharePointHost, _http, ct).ConfigureAwait(false);
        var app = PublicClientApplicationBuilder.Create(BootstrapClientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, _tenantId)
            .WithRedirectUri(DelegatedTokenProvider.RedirectUri)
            .Build();
        OnProgress?.Invoke("Opening the browser. Sign in with an administrator account.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            var result = await app.AcquireTokenInteractive(new[] { "https://graph.microsoft.com/Application.ReadWrite.All" })
                .WithUseEmbeddedWebView(false)
                .WithPrompt(Prompt.SelectAccount)
                .WithSystemWebViewOptions(new SystemWebViewOptions
                {
                    HtmlMessageSuccess = Page("Signed in.", "Return to CleverPoint Migrator. Another browser tab will open for approval."),
                    HtmlMessageError = Page("Sign-in did not complete.", "Return to CleverPoint Migrator for details."),
                })
                .ExecuteAsync(timeout.Token).ConfigureAwait(false);
            _token = result.AccessToken;
            OnProgress?.Invoke($"Signed in as {result.Account.Username}.");
            return result.Account.Username;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Sign-in timed out after 5 minutes.");
        }
        catch (MsalException ex)
        {
            var message = ex.Message ?? "";
            if (message.Contains("AADSTS65001") || message.Contains("AADSTS90094") || ex.ErrorCode == "access_denied")
                throw new InvalidOperationException(
                    "This account is not allowed to manage app registrations. Sign in with a Global Administrator, " +
                    "Privileged Role Administrator, Cloud Application Administrator or Application Administrator.", ex);
            throw new InvalidOperationException(DelegatedTokenProvider.Explain(ex), ex);
        }
    }

    /// <summary>
    /// Step 2: find the tenant's sign-in app, or create it when it does not exist. An existing app only
    /// gets what is missing added; nothing an administrator changed is removed.
    /// </summary>
    public async Task<SignInAppResult> EnsureSignInAppAsync(CancellationToken ct = default)
    {
        var scopeIds = await ResolveSharePointPermissionIdsAsync("oauth2PermissionScopes", DelegatedScopes, ct).ConfigureAwait(false);
        var (app, created) = await FindOrCreateAppAsync(SignInAppUniqueName, SignInAppDisplayName, new JsonObject
        {
            ["isFallbackPublicClient"] = true,
            ["publicClient"] = new JsonObject { ["redirectUris"] = new JsonArray(DelegatedTokenProvider.RedirectUri) },
            ["requiredResourceAccess"] = new JsonArray(ResourceAccess(scopeIds, "Scope")),
        }, ct).ConfigureAwait(false);

        var appId = app["appId"]!.GetValue<string>();
        if (!created)
        {
            var patch = new JsonObject();
            if (app["isFallbackPublicClient"]?.GetValue<bool?>() != true) patch["isFallbackPublicClient"] = true;
            AddMissingRedirect(app, patch);
            AddMissingPermissions(app, patch, scopeIds, "Scope");
            if (patch.Count > 0)
            {
                await GraphAsync(HttpMethod.Patch, $"/applications/{app["id"]!.GetValue<string>()}", patch, ct).ConfigureAwait(false);
                OnProgress?.Invoke("Updated the existing app with the settings it was missing.");
            }
        }
        await EnsureServicePrincipalAsync(appId, ct).ConfigureAwait(false);
        return new SignInAppResult(TenantId, appId, created);
    }

    /// <summary>
    /// Opt-in: find or create the unattended (app-only) app and issue a new certificate for this machine.
    /// Certificates issued earlier, for other machines, stay valid.
    /// </summary>
    public async Task<UnattendedAppResult> EnsureUnattendedAppAsync(string certificateFolder, CancellationToken ct = default)
    {
        var roleIds = await ResolveSharePointPermissionIdsAsync("appRoles", ApplicationRoles, ct).ConfigureAwait(false);
        var (app, created) = await FindOrCreateAppAsync(UnattendedAppUniqueName, UnattendedAppDisplayName, new JsonObject
        {
            // The loopback address is only where Microsoft's consent page hands control back.
            ["publicClient"] = new JsonObject { ["redirectUris"] = new JsonArray(DelegatedTokenProvider.RedirectUri) },
            ["requiredResourceAccess"] = new JsonArray(ResourceAccess(roleIds, "Role")),
        }, ct).ConfigureAwait(false);

        var objectId = app["id"]!.GetValue<string>();
        var appId = app["appId"]!.GetValue<string>();
        if (!created)
        {
            var patch = new JsonObject();
            AddMissingRedirect(app, patch);
            AddMissingPermissions(app, patch, roleIds, "Role");
            if (patch.Count > 0)
            {
                await GraphAsync(HttpMethod.Patch, $"/applications/{objectId}", patch, ct).ConfigureAwait(false);
                OnProgress?.Invoke("Updated the existing unattended app with the settings it was missing.");
            }
        }

        // Self-signed certificate, two years. The private key never leaves this machine.
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={UnattendedAppDisplayName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow.AddYears(2));
        var pfxPassword = Base64Url(RandomNumberGenerator.GetBytes(24));
        Directory.CreateDirectory(certificateFolder);
        var pfxPath = Path.Combine(certificateFolder,
            $"{UnattendedAppUniqueName}-{TenantId[..8]}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.pfx");

        // Add the public key next to the keys already registered. Graph replaces the whole list on
        // PATCH, so every existing key is sent back; one that cannot be read back stops the update
        // rather than being dropped, which would cut off another machine.
        var withKeys = await GraphAsync(HttpMethod.Get, $"/applications/{objectId}?$select=keyCredentials", null, ct).ConfigureAwait(false);
        var keys = new JsonArray();
        foreach (var existing in withKeys!["keyCredentials"]?.AsArray() ?? new JsonArray())
        {
            if (existing?["key"]?.GetValue<string>() is not { Length: > 0 } publicKey)
                throw new InvalidOperationException(
                    "The unattended app already has a certificate that could not be read back, so adding another one " +
                    "could remove it. Add this machine's certificate in the Entra admin center instead.");
            keys.Add(new JsonObject
            {
                ["type"] = existing["type"]?.GetValue<string>(),
                ["usage"] = existing["usage"]?.GetValue<string>(),
                ["key"] = publicKey,
                ["displayName"] = existing["displayName"]?.GetValue<string>(),
            });
        }
        keys.Add(new JsonObject
        {
            ["type"] = "AsymmetricX509Cert",
            ["usage"] = "Verify",
            ["key"] = Convert.ToBase64String(cert.RawData),
            ["displayName"] = $"CleverPoint Migrator on {Environment.MachineName}",
        });
        await GraphAsync(HttpMethod.Patch, $"/applications/{objectId}", new JsonObject { ["keyCredentials"] = keys }, ct).ConfigureAwait(false);
        await File.WriteAllBytesAsync(pfxPath, cert.Export(X509ContentType.Pfx, pfxPassword), ct).ConfigureAwait(false);
        OnProgress?.Invoke($"Certificate registered (expires {cert.NotAfter:yyyy-MM-dd}).");

        await EnsureServicePrincipalAsync(appId, ct).ConfigureAwait(false);
        return new UnattendedAppResult(TenantId, appId, created, pfxPath, pfxPassword, cert.Thumbprint, cert.NotAfter.ToUniversalTime());
    }

    /// <summary>
    /// Step 3: open Microsoft's admin consent page for the app and wait for the administrator's answer.
    /// Throws when consent is declined or not answered within five minutes.
    /// </summary>
    public async Task GrantAdminConsentAsync(string tenantId, string appId, bool appJustCreated, CancellationToken ct = default)
    {
        if (appJustCreated)
        {
            // A brand-new registration takes a moment to reach the sign-in service. Opening the page
            // too early shows "application not found" with no way back to this app.
            OnProgress?.Invoke("Waiting for Microsoft Entra to finish creating the app...");
            await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        }

        var port = FreeLoopbackPort();
        var redirect = $"http://localhost:{port}";
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        using var listener = new HttpListener();
        listener.Prefixes.Add(redirect + "/");
        listener.Start();
        try
        {
            OnProgress?.Invoke("Opening Microsoft's approval page. Review the permissions and choose Accept.");
            OpenBrowser(BuildAdminConsentUrl(tenantId, appId, redirect, state));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            while (true)
            {
                var pending = listener.GetContextAsync();
                var finished = await Task.WhenAny(pending, Task.Delay(Timeout.Infinite, timeout.Token)).ConfigureAwait(false);
                if (finished != pending)
                {
                    ct.ThrowIfCancellationRequested();
                    throw new TimeoutException(
                        "No answer from the approval page after 5 minutes. If it showed an error, wait a minute and run the setup again.");
                }
                var context = await pending.ConfigureAwait(false);
                var query = context.Request.QueryString;
                var consent = query["admin_consent"];
                var error = query["error_description"] ?? query["error"];
                if (consent is null && error is null)
                {
                    // A browser asking for the favicon, not the answer.
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    continue;
                }
                var ok = error is null && string.Equals(consent, "True", StringComparison.OrdinalIgnoreCase) && query["state"] == state;
                var html = Encoding.UTF8.GetBytes(ok
                    ? Page("Approved.", "You can close this tab and return to CleverPoint Migrator.")
                    : Page("Not approved.", "Return to CleverPoint Migrator for details."));
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.OutputStream.WriteAsync(html, ct).ConfigureAwait(false);
                context.Response.Close();
                if (ok)
                {
                    OnProgress?.Invoke("Approved for the whole organization.");
                    return;
                }
                throw new InvalidOperationException(error is null
                    ? "The approval page returned an unexpected answer. Run the setup again."
                    : $"The app was not approved: {error.Split('\n')[0].Trim()}");
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// The link an administrator opens to approve the app. Without <paramref name="redirectUri"/> it is
    /// the shareable form: it can be approved from any browser, on any computer.
    /// </summary>
    public static string BuildAdminConsentUrl(string tenantId, string appId, string? redirectUri = null, string? state = null)
    {
        var url = $"https://login.microsoftonline.com/{Uri.EscapeDataString(tenantId)}/adminconsent?client_id={Uri.EscapeDataString(appId)}" +
                  $"&redirect_uri={Uri.EscapeDataString(redirectUri ?? DelegatedTokenProvider.RedirectUri)}";
        return state is null ? url : url + "&state=" + Uri.EscapeDataString(state);
    }

    // ---- Graph plumbing ---------------------------------------------------------------------------

    private async Task<(JsonNode App, bool Created)> FindOrCreateAppAsync(string uniqueName, string displayName, JsonObject settings, CancellationToken ct)
    {
        const string select = "$select=id,appId,displayName,isFallbackPublicClient,publicClient,requiredResourceAccess";
        var byKey = $"/applications(uniqueName='{uniqueName}')";

        // 1. The fixed unique name: the same on every computer, so a second setup finds the first one.
        var (status, found) = await GraphTryAsync(HttpMethod.Get, $"{byKey}?{select}", null, null, ct).ConfigureAwait(false);
        if (status == HttpStatusCode.OK)
        {
            OnProgress?.Invoke($"Found the existing app \"{found!["displayName"]}\" ({found["appId"]}). Reusing it.");
            return (found, false);
        }
        var uniqueNameWorks = status == HttpStatusCode.NotFound;

        // 2. The tag, for a directory where the unique-name lookup is not available.
        if (!uniqueNameWorks)
        {
            var tagged = await GraphAsync(HttpMethod.Get, $"/applications?$filter=tags/any(t:t eq '{uniqueName}')&{select}", null, ct).ConfigureAwait(false);
            if (tagged!["value"]!.AsArray().FirstOrDefault() is { } byTag)
            {
                OnProgress?.Invoke($"Found the existing app \"{byTag["displayName"]}\" ({byTag["appId"]}). Reusing it.");
                return (byTag.DeepClone(), false);
            }
        }

        // 3. Create. Create-if-missing on the unique name means two computers doing this at the same
        //    moment still end up with one registration.
        var body = (JsonObject)settings.DeepClone();
        body["displayName"] = displayName;
        body["signInAudience"] = "AzureADMyOrg";
        body["tags"] = new JsonArray(uniqueName);
        JsonNode? created;
        if (uniqueNameWorks)
        {
            (status, created) = await GraphTryAsync(HttpMethod.Patch, byKey, body, "create-if-missing", ct).ConfigureAwait(false);
            if ((int)status >= 400)
                throw new InvalidOperationException($"Creating the app registration failed (HTTP {(int)status}): {ErrorText(created)}");
        }
        else
        {
            created = await GraphAsync(HttpMethod.Post, "/applications", body, ct).ConfigureAwait(false);
        }

        // Read it back; a new object can take a few seconds to become visible.
        for (var attempt = 0; created?["appId"] is null && attempt < 10; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            (status, created) = await GraphTryAsync(HttpMethod.Get, $"{byKey}?{select}", null, null, ct).ConfigureAwait(false);
            if (status != HttpStatusCode.OK) created = null;
        }
        if (created?["appId"] is null)
            throw new InvalidOperationException("The app registration was created but could not be read back. Run the setup again in a minute.");
        OnProgress?.Invoke($"Created the app \"{displayName}\" ({created["appId"]}).");
        return (created, true);
    }

    /// <summary>IDs of SharePoint permissions by name, read from the tenant so nothing is hard-coded.</summary>
    private async Task<List<string>> ResolveSharePointPermissionIdsAsync(string collection, string[] names, CancellationToken ct)
    {
        var doc = await GraphAsync(HttpMethod.Get,
            $"/servicePrincipals?$filter=appId eq '{SharePointResourceAppId}'&$select=id,{collection}", null, ct).ConfigureAwait(false);
        var sharePoint = doc!["value"]!.AsArray().FirstOrDefault()
            ?? throw new InvalidOperationException("SharePoint Online was not found in this tenant.");
        var ids = new List<string>();
        foreach (var name in names)
        {
            var match = sharePoint[collection]!.AsArray().FirstOrDefault(p => p?["value"]?.GetValue<string>() == name)
                ?? throw new InvalidOperationException($"SharePoint Online does not offer the permission '{name}' in this tenant.");
            ids.Add(match["id"]!.GetValue<string>());
        }
        return ids;
    }

    private static JsonObject ResourceAccess(IEnumerable<string> ids, string type) => new()
    {
        ["resourceAppId"] = SharePointResourceAppId,
        ["resourceAccess"] = new JsonArray(ids.Select(id => (JsonNode)new JsonObject { ["id"] = id, ["type"] = type }).ToArray()),
    };

    private static void AddMissingRedirect(JsonNode app, JsonObject patch)
    {
        var uris = app["publicClient"]?["redirectUris"]?.AsArray().Select(u => u?.GetValue<string>() ?? "").ToList() ?? new List<string>();
        if (uris.Any(u => u.TrimEnd('/').Equals(DelegatedTokenProvider.RedirectUri, StringComparison.OrdinalIgnoreCase))) return;
        uris.Add(DelegatedTokenProvider.RedirectUri);
        patch["publicClient"] = new JsonObject { ["redirectUris"] = new JsonArray(uris.Select(u => (JsonNode)u).ToArray()) };
    }

    /// <summary>Adds the permissions that are not requested yet, keeping every existing entry as it is.</summary>
    private static void AddMissingPermissions(JsonNode app, JsonObject patch, List<string> ids, string type)
    {
        var all = (JsonArray)(app["requiredResourceAccess"]?.DeepClone() ?? new JsonArray());
        var sharePoint = all.FirstOrDefault(r => r?["resourceAppId"]?.GetValue<string>() == SharePointResourceAppId);
        if (sharePoint is null)
        {
            all.Add(ResourceAccess(ids, type));
            patch["requiredResourceAccess"] = all;
            return;
        }
        var access = sharePoint["resourceAccess"]!.AsArray();
        var have = access.Select(a => a?["id"]?.GetValue<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = ids.Where(id => !have.Contains(id)).ToList();
        if (missing.Count == 0) return;
        foreach (var id in missing) access.Add(new JsonObject { ["id"] = id, ["type"] = type });
        patch["requiredResourceAccess"] = all;
    }

    /// <summary>The app's identity inside the tenant (enterprise application). Consent attaches to it.</summary>
    private async Task EnsureServicePrincipalAsync(string appId, CancellationToken ct)
    {
        var lookup = await GraphAsync(HttpMethod.Get, $"/servicePrincipals?$filter=appId eq '{appId}'&$select=id", null, ct).ConfigureAwait(false);
        if (lookup!["value"]!.AsArray().Count > 0) return;
        for (var attempt = 1; ; attempt++)
        {
            var (status, body) = await GraphTryAsync(HttpMethod.Post, "/servicePrincipals", new JsonObject { ["appId"] = appId }, null, ct).ConfigureAwait(false);
            if ((int)status < 300 || status == HttpStatusCode.Conflict) return;
            // A just-created registration is not always visible to this call straight away.
            if (attempt >= 8)
                throw new InvalidOperationException($"Creating the enterprise application failed (HTTP {(int)status}): {ErrorText(body)}");
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }
    }

    private async Task<JsonNode?> GraphAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        var (status, node) = await GraphTryAsync(method, path, body, null, ct).ConfigureAwait(false);
        if ((int)status >= 400)
        {
            if (status == HttpStatusCode.Forbidden)
                throw new InvalidOperationException(
                    "This account is not allowed to manage app registrations in this tenant. Sign in with a Global Administrator, " +
                    "Privileged Role Administrator, Cloud Application Administrator or Application Administrator.");
            throw new InvalidOperationException($"Microsoft Graph {method} {path.Split('?')[0]} failed (HTTP {(int)status}): {ErrorText(node)}");
        }
        return node;
    }

    private async Task<(HttpStatusCode Status, JsonNode? Body)> GraphTryAsync(HttpMethod method, string path, JsonNode? body, string? prefer, CancellationToken ct)
    {
        if (_token is null) throw new InvalidOperationException("Sign in as an administrator first.");
        using var request = new HttpRequestMessage(method, GraphBase + path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
        if (prefer != null) request.Headers.TryAddWithoutValidation("Prefer", prefer);
        if (body != null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        JsonNode? node = null;
        if (content.Length > 0)
        {
            try { node = JsonNode.Parse(content); } catch (JsonException) { node = JsonValue.Create(content); }
        }
        return (response.StatusCode, node);
    }

    private static string ErrorText(JsonNode? body)
    {
        if (body is JsonObject o && o["error"]?["message"]?.GetValue<string>() is { } message) return message;
        var raw = body?.ToJsonString() ?? "(no details)";
        return raw.Length > 300 ? raw[..300] : raw;
    }

    private static int FreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
        finally { probe.Stop(); }
    }

    private static void DefaultOpenBrowser(string url) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });

    private static string Page(string title, string text) =>
        $"<html><body style='font-family:Segoe UI,sans-serif;margin:40px'><h2>{title}</h2><p>{text}</p></body></html>";

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

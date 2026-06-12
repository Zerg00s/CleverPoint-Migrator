using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace CleverPoint.Migrator.Core.Auth;

/// <summary>
/// App-only access tokens for SharePoint Online via the Entra ID
/// client-credentials flow with a certificate (the only app-only flow SPO
/// accepts for REST/CSOM). Tokens are cached per resource host and renewed
/// a few minutes before expiry.
/// </summary>
public class CertTokenProvider : ITokenProvider, IDisposable
{
    private readonly AppCredentials _creds;
    private readonly X509Certificate2 _cert;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset Expires)> _cache = new();

    public CertTokenProvider(AppCredentials creds, HttpClient? http = null)
    {
        _creds = creds;
        _http = http ?? new HttpClient();
        if (string.IsNullOrEmpty(creds.CertPfxPath) || !File.Exists(creds.CertPfxPath))
            throw new FileNotFoundException($"Certificate PFX not found: '{creds.CertPfxPath}'");
        _cert = new X509Certificate2(creds.CertPfxPath, creds.CertPassword, X509KeyStorageFlags.EphemeralKeySet);
    }

    /// <summary>Returns a Bearer token whose audience matches the given SharePoint host (e.g. tenant.sharepoint.com).</summary>
    public async Task<string> GetTokenAsync(string sharePointHost)
    {
        if (_cache.TryGetValue(sharePointHost, out var cached) && cached.Expires > DateTimeOffset.UtcNow.AddMinutes(5))
            return cached.Token;

        var tokenUrl = $"https://login.microsoftonline.com/{_creds.TenantId}/oauth2/v2.0/token";
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = _creds.AppId,
            ["scope"] = $"https://{sharePointHost}/.default",
            ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
            ["client_assertion"] = BuildClientAssertion(tokenUrl),
        });

        var response = await _http.PostAsync(tokenUrl, form);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Token request for {sharePointHost} failed (HTTP {(int)response.StatusCode}): {body}");

        using var json = JsonDocument.Parse(body);
        var token = json.RootElement.GetProperty("access_token").GetString()!;
        var expiresIn = json.RootElement.GetProperty("expires_in").GetInt32();
        _cache[sharePointHost] = (token, DateTimeOffset.UtcNow.AddSeconds(expiresIn));
        return token;
    }

    private string BuildClientAssertion(string audience)
    {
        var now = DateTimeOffset.UtcNow;
        var header = new Dictionary<string, object>
        {
            ["alg"] = "RS256",
            ["typ"] = "JWT",
            ["x5t"] = Base64Url(_cert.GetCertHash()),
        };
        var payload = new Dictionary<string, object>
        {
            ["aud"] = audience,
            ["iss"] = _creds.AppId,
            ["sub"] = _creds.AppId,
            ["jti"] = Guid.NewGuid().ToString(),
            ["nbf"] = now.AddMinutes(-5).ToUnixTimeSeconds(),
            ["exp"] = now.AddMinutes(10).ToUnixTimeSeconds(),
        };

        var unsigned = Base64Url(JsonSerializer.SerializeToUtf8Bytes(header)) + "." +
                       Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
        using var rsa = _cert.GetRSAPrivateKey()
            ?? throw new InvalidOperationException("Certificate has no RSA private key.");
        var signature = rsa.SignData(Encoding.UTF8.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return unsigned + "." + Base64Url(signature);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose() => _cert.Dispose();
}

public interface ITokenProvider
{
    Task<string> GetTokenAsync(string sharePointHost);
}

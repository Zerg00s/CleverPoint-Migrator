using System.Security.Cryptography.X509Certificates;
using CleverPoint.Migrator.Core.Auth;
using CleverPoint.Migrator.Core.Csom;

namespace CleverPoint.Migrator.Ux.Services;

/// <summary>
/// Turns a saved connection into a live <see cref="SpConnection"/> and caches
/// the token provider per tenant+app so authentication is REUSED across every
/// site collection and migration on the same tenant. The cert flow refreshes
/// its own access token before expiry, so no re-prompt is ever needed for it.
/// Browser connections can't be authenticated inside this WebView host.
/// </summary>
public static class UxConnectionResolver
{
    // Key = tenant|app|pfx. One CertTokenProvider per app identity; it caches and
    // refreshes the access token internally, shared by all site collections.
    private static readonly Dictionary<string, CertTokenProvider> TokenCache = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsCert(SavedConnection c) => c.AuthMode == "AppCertificate";

    public static bool CanRunHeadless(SavedConnection? source, SavedConnection? target)
        => source is not null && target is not null && IsCert(source) && IsCert(target);

    public static SpConnection ResolveCert(SavedConnection c, string siteUrl)
    {
        if (!IsCert(c))
            throw new InvalidOperationException($"'{c.Name}' is a browser connection. Interactive sign-in can't run inside this app; use an app + certificate connection.");
        var key = $"{c.TenantId}|{c.AppId}|{c.CertPfxPath}";
        if (!TokenCache.TryGetValue(key, out var provider))
        {
            provider = new CertTokenProvider(new AppCredentials
            {
                TenantId = c.TenantId,
                AppId = c.AppId,
                CertPfxPath = c.CertPfxPath,
                CertPassword = UxSecret.Unprotect(c.CertPasswordProtected),
            });
            TokenCache[key] = provider;
        }
        return new SpConnection(siteUrl.TrimEnd('/'), provider);
    }

    /// <summary>Browser (cookie) connection from a captured FedAuth/rtFa session.</summary>
    public static SpConnection ResolveBrowser(string siteUrl, string fedAuth, string rtFa)
        => new(siteUrl.TrimEnd('/'), fedAuth, rtFa);

    /// <summary>
    /// Resolve any connection: app+certificate silently, browser from a captured
    /// session. Throws with a clear message if a browser connection isn't signed in.
    /// </summary>
    public static SpConnection Resolve(SavedConnection c, string siteUrl, BrowserSignIn browser)
    {
        if (IsCert(c)) return ResolveCert(c, siteUrl);
        var s = browser.GetSession(siteUrl)
            ?? throw new InvalidOperationException($"Sign in to '{c.Name}' first (Connect).");
        return ResolveBrowser(siteUrl, s.FedAuth, s.RtFa);
    }

    public static bool CanResolve(SavedConnection? c, BrowserSignIn browser)
        => c is not null && (IsCert(c) || browser.HasSession(c.SiteUrl));

    /// <summary>Drops the cached token so the next use re-acquires it (Reconnect).</summary>
    public static void Invalidate(SavedConnection c) =>
        TokenCache.Remove($"{c.TenantId}|{c.AppId}|{c.CertPfxPath}");

    public static SavedConnection? Find(UxSettings settings, string siteUrl)
    {
        return settings.Connections.FirstOrDefault(c =>
            siteUrl.StartsWith(new Uri(c.SiteUrl).GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
            ?? settings.Connections.FirstOrDefault(c => c.SiteUrl.Equals(siteUrl, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Verifies a connection live. App+certificate: acquires a token and reads
    /// the web title, records the cert expiry. Browser: reports it can't be
    /// tested in this host. Mutates LastStatus / LastVerifiedUtc / CertExpiresUtc.
    /// </summary>
    public static async Task<(bool Ok, string Message)> TestAsync(SavedConnection c)
    {
        if (!IsCert(c))
        {
            c.LastStatus = "Browser sign-in (desktop app only)";
            c.LastVerifiedUtc = DateTime.UtcNow;
            return (false, "Browser connections can't be verified in this app. Use app + certificate, or the desktop app for browser sign-in.");
        }
        try
        {
            CaptureCertExpiry(c);
            var conn = ResolveCert(c, c.SiteUrl);
            using var doc = await conn.Rest.GetJsonAsync($"{conn.SiteUrl}/_api/web?$select=Title");
            var title = doc.RootElement.GetProperty("Title").GetString();
            c.LastStatus = $"Connected ({title})";
            c.LastVerifiedUtc = DateTime.UtcNow;
            return (true, $"Connected to '{title}'.");
        }
        catch (Exception ex)
        {
            Invalidate(c);
            var brief = ex.Message.Length > 160 ? ex.Message[..160] : ex.Message;
            c.LastStatus = $"Failed: {brief}";
            c.LastVerifiedUtc = DateTime.UtcNow;
            return (false, brief);
        }
    }

    private static void CaptureCertExpiry(SavedConnection c)
    {
        try
        {
            if (!File.Exists(c.CertPfxPath)) return;
            using var cert = new X509Certificate2(c.CertPfxPath, UxSecret.Unprotect(c.CertPasswordProtected), X509KeyStorageFlags.EphemeralKeySet);
            c.CertExpiresUtc = cert.NotAfter.ToUniversalTime();
        }
        catch { /* expiry display is best-effort */ }
    }

    /// <summary>True when a cert connection is verified and not within 7 days of expiry.</summary>
    public static bool IsHealthy(SavedConnection c) =>
        c.LastStatus.StartsWith("Connected", StringComparison.OrdinalIgnoreCase)
        && (c.CertExpiresUtc is null || c.CertExpiresUtc > DateTime.UtcNow.AddDays(7));
}

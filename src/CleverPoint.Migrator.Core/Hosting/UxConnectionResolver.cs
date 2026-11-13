using System.Security.Cryptography.X509Certificates;
using CleverPoint.Migrator.Core.Auth;
using CleverPoint.Migrator.Core.Csom;

namespace CleverPoint.Migrator.Core.Hosting;

/// <summary>
/// Turns a saved connection into a live <see cref="SpConnection"/> and caches the token provider per
/// tenant and app, so authentication is reused across every site collection and migration on the same
/// tenant. Three kinds of connection exist:
///   - Browser: SharePoint cookies captured by the sign-in helper window (Windows only, 18 hours);
///   - AppDelegated ("Microsoft sign-in"): the tenant's own app registration and the signed-in user's
///     rights. Tokens renew silently from the encrypted cache, also in the middle of a long run;
///   - AppCertificate: app-only with a certificate, no user involved.
/// </summary>
public static class UxConnectionResolver
{
    // Key = tenant|app|pfx. One CertTokenProvider per app identity; it caches and
    // refreshes the access token internally, shared by all site collections.
    private static readonly Dictionary<string, CertTokenProvider> TokenCache = new(StringComparer.OrdinalIgnoreCase);

    // Key = tenant|app. One provider per tenant app; the signed-in account lives in the shared cache file.
    private static readonly Dictionary<string, DelegatedTokenProvider> DelegatedCache = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsCert(SavedConnection c) => c.AuthMode == "AppCertificate";
    public static bool IsDelegated(SavedConnection c) => c.AuthMode == "AppDelegated";
    public static bool IsBrowser(SavedConnection c) => !IsCert(c) && !IsDelegated(c);

    /// <summary>A delegated connection that has signed in on this machine. The token may still need renewing.</summary>
    public static bool IsSignedIn(SavedConnection c) => IsDelegated(c) && !string.IsNullOrEmpty(c.HomeAccountId);

    /// <summary>Can run with nobody at the keyboard: certificate, or a delegated sign-in that is already cached.</summary>
    public static bool CanRunHeadless(SavedConnection? source, SavedConnection? target)
        => source is not null && target is not null
           && (IsCert(source) || IsSignedIn(source)) && (IsCert(target) || IsSignedIn(target));

    public static SpConnection ResolveCert(SavedConnection c, string siteUrl)
    {
        if (!IsCert(c))
            throw new InvalidOperationException($"'{c.Name}' is not an app + certificate connection.");
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

    /// <summary>The token provider behind a "Microsoft sign-in" connection.</summary>
    public static DelegatedTokenProvider DelegatedProvider(SavedConnection c)
    {
        if (!IsDelegated(c))
            throw new InvalidOperationException($"'{c.Name}' is not a Microsoft sign-in connection.");
        if (string.IsNullOrWhiteSpace(c.TenantId) || string.IsNullOrWhiteSpace(c.AppId))
            throw new InvalidOperationException($"'{c.Name}' has no app (client) ID yet. Set it up or enter the ID first.");
        var key = $"{c.TenantId}|{c.AppId}";
        lock (DelegatedCache)
        {
            if (!DelegatedCache.TryGetValue(key, out var provider))
            {
                provider = new DelegatedTokenProvider(c.TenantId, c.AppId, UxSettings.TokenCachePath, c.HomeAccountId);
                DelegatedCache[key] = provider;
            }
            return provider;
        }
    }

    public static SpConnection ResolveDelegated(SavedConnection c, string siteUrl)
    {
        if (!IsSignedIn(c))
            throw new InvalidOperationException($"Sign in to '{c.Name}' first (Connect).");
        return new SpConnection(siteUrl.TrimEnd('/'), DelegatedProvider(c));
    }

    /// <summary>
    /// Opens the system browser for the user to sign in to a delegated connection and records who
    /// signed in. The caller saves the settings.
    /// </summary>
    public static async Task<(bool Ok, string Message)> SignInDelegatedAsync(SavedConnection c, CancellationToken ct = default)
    {
        try
        {
            var account = await DelegatedProvider(c).SignInAsync(new Uri(c.SiteUrl).Host, ct);
            c.AccountUpn = account.Username;
            c.HomeAccountId = account.HomeAccountId;
            return (true, $"Signed in as {account.Username}.");
        }
        catch (Exception ex)
        {
            return (false, Brief(ex.Message));
        }
    }

    /// <summary>
    /// Makes sure a delegated connection can get a token right now: silently when the cached sign-in is
    /// still good, otherwise through the browser. Returns true when a new sign-in happened (settings
    /// changed and need saving). Throws with a clear message when the user does not complete it.
    /// </summary>
    public static async Task<bool> EnsureDelegatedAsync(SavedConnection c, string siteUrl, CancellationToken ct = default)
    {
        var host = new Uri(siteUrl).Host;
        if (IsSignedIn(c))
        {
            try
            {
                await DelegatedProvider(c).GetTokenAsync(host);
                return false;
            }
            catch (SignInRequiredException) { /* expired or revoked: fall through to the browser */ }
        }
        var (ok, message) = await SignInDelegatedAsync(c, ct);
        if (!ok) throw new InvalidOperationException($"Sign-in required for {host} ({c.Name}): {message}");
        return true;
    }

    /// <summary>Forgets the delegated sign-in on this machine. The caller saves the settings.</summary>
    public static async Task SignOutAsync(SavedConnection c)
    {
        if (!IsDelegated(c)) return;
        try { await DelegatedProvider(c).SignOutAsync(); } catch { /* nothing cached is fine */ }
        c.AccountUpn = "";
        c.HomeAccountId = "";
        c.LastStatus = "";
        Invalidate(c);
    }

    /// <summary>Browser (cookie) connection from a captured FedAuth/rtFa session.</summary>
    public static SpConnection ResolveBrowser(string siteUrl, string fedAuth, string rtFa)
        => new(siteUrl.TrimEnd('/'), fedAuth, rtFa);

    /// <summary>
    /// Resolve any connection: app + certificate and Microsoft sign-in silently, browser from a captured
    /// session. Throws with a clear message if the connection isn't signed in.
    /// </summary>
    public static SpConnection Resolve(SavedConnection c, string siteUrl, BrowserSignIn browser)
    {
        if (IsCert(c)) return ResolveCert(c, siteUrl);
        if (IsDelegated(c)) return ResolveDelegated(c, siteUrl);
        var s = browser.GetSession(siteUrl)
            ?? throw new InvalidOperationException($"Sign in to '{c.Name}' first (Connect).");
        return ResolveBrowser(siteUrl, s.FedAuth, s.RtFa);
    }

    public static bool CanResolve(SavedConnection? c, BrowserSignIn browser)
        => c is not null && (IsCert(c) || IsSignedIn(c) || (IsBrowser(c) && browser.HasSession(c.SiteUrl)));

    /// <summary>Drops the cached token provider so the next use builds a new one (Reconnect).</summary>
    public static void Invalidate(SavedConnection c)
    {
        TokenCache.Remove($"{c.TenantId}|{c.AppId}|{c.CertPfxPath}");
        lock (DelegatedCache) DelegatedCache.Remove($"{c.TenantId}|{c.AppId}");
    }

    /// <summary>
    /// The saved connection to use for a site. Connections cover a whole tenant, so when several fit,
    /// the one that needs nobody at the keyboard wins: app + certificate, then a Microsoft sign-in that
    /// is already cached, then browser sign-in. Without this a site saved with browser sign-in asked
    /// for a sign-in even though the tenant already had a working certificate connection.
    /// </summary>
    public static SavedConnection? Find(UxSettings settings, string siteUrl)
    {
        if (!Uri.TryCreate(siteUrl, UriKind.Absolute, out var site)) return null;
        // contoso, contoso-my and contoso-admin are one tenant; an app connection serves all three.
        static string Tenant(string host)
        {
            var dot = host.IndexOf('.');
            var (name, rest) = dot < 0 ? (host, "") : (host[..dot], host[dot..]);
            if (name.EndsWith("-my", StringComparison.OrdinalIgnoreCase)) name = name[..^3];
            else if (name.EndsWith("-admin", StringComparison.OrdinalIgnoreCase)) name = name[..^6];
            return (name + rest).ToLowerInvariant();
        }
        return settings.Connections
            .Where(c => Uri.TryCreate(c.SiteUrl, UriKind.Absolute, out var saved)
                        && (string.Equals(saved.Host, site.Host, StringComparison.OrdinalIgnoreCase)
                            || (!IsBrowser(c) && Tenant(saved.Host) == Tenant(site.Host))))
            .OrderBy(c => IsCert(c) ? 0 : IsSignedIn(c) ? 1 : 2)
            .FirstOrDefault();
    }

    /// <summary>
    /// Verifies a connection live by reading the web title. App + certificate also records the
    /// certificate expiry. Microsoft sign-in uses the cached sign-in and never opens a browser here.
    /// Browser connections are verified by signing in, not by this method.
    /// Mutates LastStatus / LastVerifiedUtc / CertExpiresUtc.
    /// </summary>
    public static async Task<(bool Ok, string Message)> TestAsync(SavedConnection c)
    {
        if (IsBrowser(c))
        {
            c.LastStatus = "Browser sign-in";
            c.LastVerifiedUtc = DateTime.UtcNow;
            return (false, "Browser connections are checked by signing in (Connect).");
        }
        try
        {
            SpConnection conn;
            if (IsCert(c))
            {
                CaptureCertExpiry(c);
                conn = ResolveCert(c, c.SiteUrl);
            }
            else
            {
                conn = ResolveDelegated(c, c.SiteUrl);
            }
            using var doc = await conn.Rest.GetJsonAsync($"{conn.SiteUrl}/_api/web?$select=Title");
            var title = doc.RootElement.GetProperty("Title").GetString();
            c.LastStatus = $"Connected ({title})";
            c.LastVerifiedUtc = DateTime.UtcNow;
            return (true, $"Connected to '{title}'.");
        }
        catch (Exception ex)
        {
            Invalidate(c);
            var brief = Brief(ex.Message);
            c.LastStatus = $"Failed: {brief}";
            c.LastVerifiedUtc = DateTime.UtcNow;
            return (false, brief);
        }
    }

    private static string Brief(string message) => message.Length > 200 ? message[..200] : message;

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

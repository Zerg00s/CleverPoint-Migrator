using CleverPoint.Migrator.App.Screens;
using CleverPoint.Migrator.Core.Auth;
using CleverPoint.Migrator.Core.Csom;

namespace CleverPoint.Migrator.App.Services;

/// <summary>
/// One place that turns a site URL into a connected SpConnection using the
/// saved connection's auth mode: App+Certificate connects silently; Browser
/// pops the WebView2 sign-in (cookies cached per host for the session, and
/// re-prompted on the spot when a session expires).
/// </summary>
public static class ConnectionResolver
{
    private static readonly Dictionary<string, SpConnection> BrowserSessions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> FreshSignInHosts = new(StringComparer.OrdinalIgnoreCase);

    public static SpConnection Resolve(IWin32Window owner, AppSettings settings, string siteUrl)
    {
        siteUrl = siteUrl.TrimEnd('/');
        var host = new Uri(siteUrl).Host;
        Core.Http.RequestThrottle.Configure(host, settings.MaxRequestsPerSecond);

        var saved = settings.Connections.FirstOrDefault(c =>
            siteUrl.StartsWith(new Uri(c.SiteUrl).GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase));

        if (saved is { AuthMode: "AppCertificate" })
        {
            return new SpConnection(siteUrl, new CertTokenProvider(new AppCredentials
            {
                TenantId = saved.TenantId,
                AppId = saved.AppId,
                CertPfxPath = saved.CertPfxPath,
                CertPassword = string.IsNullOrEmpty(saved.CertPasswordProtected) ? "" : AppSettings.Unprotect(saved.CertPasswordProtected),
            }));
        }

        // Browser mode (explicitly saved, or no saved connection at all).
        if (BrowserSessions.TryGetValue(host, out var session))
            return session.ForWeb(siteUrl);
        // After an invalidation the WebView2 cookie jar must be wiped too,
        // or silent SSO re-captures the same dead cookies with no prompt.
        var forceFresh = FreshSignInHosts.Remove(host);
        var connected = BrowserLoginForm.Connect(owner, siteUrl, forceFresh)
            ?? throw new InvalidOperationException("Sign-in was cancelled.");
        BrowserSessions[host] = connected;
        return connected;
    }

    /// <summary>Call when a browser session 401s mid-run; the next Resolve signs in FRESH (cookie jar wiped).</summary>
    public static void InvalidateBrowserSession(string siteUrl)
    {
        var host = new Uri(siteUrl).Host;
        BrowserSessions.Remove(host);
        FreshSignInHosts.Add(host);
    }
}

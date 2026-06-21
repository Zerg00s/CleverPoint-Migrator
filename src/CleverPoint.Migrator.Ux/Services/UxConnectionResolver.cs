using CleverPoint.Migrator.Core.Auth;
using CleverPoint.Migrator.Core.Csom;

namespace CleverPoint.Migrator.Ux.Services;

/// <summary>
/// Turns a saved connection into a live <see cref="SpConnection"/>.
/// App + certificate resolves headlessly (works on Windows and WSL).
/// Browser connections require an interactive sign-in (added separately) and
/// throw here until a session exists.
/// </summary>
public static class UxConnectionResolver
{
    public static bool IsCert(SavedConnection c) => c.AuthMode == "AppCertificate";

    /// <summary>True when both endpoints can be resolved without interactive sign-in.</summary>
    public static bool CanRunHeadless(SavedConnection? source, SavedConnection? target)
        => source is not null && target is not null && IsCert(source) && IsCert(target);

    public static SpConnection ResolveCert(SavedConnection c, string siteUrl)
    {
        if (!IsCert(c))
            throw new InvalidOperationException($"'{c.Name}' is a browser connection; sign-in is required to use it.");
        var creds = new AppCredentials
        {
            TenantId = c.TenantId,
            AppId = c.AppId,
            CertPfxPath = c.CertPfxPath,
            // On Windows the WinForms app stores this DPAPI-protected; the Ux app
            // (cross-platform) stores it plain off-Windows. Use whichever is set.
            CertPassword = c.CertPasswordProtected,
        };
        return new SpConnection(siteUrl.TrimEnd('/'), new CertTokenProvider(creds));
    }

    public static SavedConnection? Find(UxSettings settings, string siteUrl)
    {
        var auth = new Uri(siteUrl.TrimEnd('/')).GetLeftPart(UriPartial.Authority);
        return settings.Connections.FirstOrDefault(c =>
            siteUrl.StartsWith(new Uri(c.SiteUrl).GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
            ?? settings.Connections.FirstOrDefault(c => c.SiteUrl.Equals(siteUrl, StringComparison.OrdinalIgnoreCase));
    }
}

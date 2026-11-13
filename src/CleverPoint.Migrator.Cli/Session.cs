using CleverPoint.Migrator.Core.Auth;
using CleverPoint.Migrator.Core.Csom;
using CleverPoint.Migrator.Core.History;
using CleverPoint.Migrator.Core.Hosting;

namespace CleverPoint.Migrator.Cli;

/// <summary>
/// What every command needs: the same settings, connections, sign-ins and history the desktop app
/// uses. The two share one settings folder, so a connection added in either is there in both, and a
/// run started here shows up in the app's Activity page.
/// </summary>
public sealed class Session
{
    public UxSettings Settings { get; }
    public BrowserSignIn Browser { get; } = new();
    public SiteBrowser Sites { get; }

    public Session()
    {
        Settings = new UxSettings();
        Settings.EnsureLoaded();
        Sites = new SiteBrowser(Settings.CacheMinutes);
    }

    public static HistoryStore OpenHistory() => new(UxSettings.HistoryDbPath);

    /// <summary>A file the user asked the tool to write: full path, with its folder created.</summary>
    public static string OutputFile(string value)
    {
        var path = Path.GetFullPath(value);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    public static string SiteUrl(string value)
    {
        var url = JobSpecs.NormalizeSite(value);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps || parsed.Host.Length == 0)
            throw CliError.Usage($"'{value}' is not a site URL.", "Use a full https URL, for example https://contoso.sharepoint.com/sites/Finance");
        if (JobSpecs.LooksLikePage(url))
            throw CliError.Usage($"'{value}' is the address of a page, not of a site.", "Use the site itself, for example https://contoso.sharepoint.com/sites/Finance");
        return url;
    }

    /// <summary>The saved connection that covers this site (connections are per tenant host).</summary>
    public SavedConnection Connection(string site) =>
        UxConnectionResolver.Find(Settings, site)
        ?? throw CliError.NotFound($"No saved connection covers {new Uri(site).Host}.",
            $"See what exists with: cpm connection list. Add one with: cpm connection add --site {site} --sign-in microsoft|browser|certificate");

    public static string SignInKind(SavedConnection c) =>
        UxConnectionResolver.IsCert(c) ? "certificate" : UxConnectionResolver.IsDelegated(c) ? "microsoft" : "browser";

    /// <summary>Signed in and usable right now without asking anyone. The cached Microsoft sign-in may still fail to renew; <see cref="ConnectAsync"/> proves it.</summary>
    public bool IsReady(SavedConnection c) =>
        UxConnectionResolver.IsCert(c) || UxConnectionResolver.IsSignedIn(c)
        || (UxConnectionResolver.IsBrowser(c) && Browser.HasSession(c.SiteUrl));

    /// <summary>
    /// A live connection to a site, without ever prompting. When the connection needs a person to sign
    /// in, this fails with the sign-in-required error that names the command the user has to run.
    /// </summary>
    public async Task<SpConnection> ConnectAsync(string site)
    {
        var connection = Connection(site);
        if (UxConnectionResolver.IsDelegated(connection))
        {
            if (!UxConnectionResolver.IsSignedIn(connection))
                throw CliError.SignIn($"Nobody is signed in to {new Uri(site).Host} on this computer yet.", site);
            try
            {
                await UxConnectionResolver.DelegatedProvider(connection).GetTokenAsync(new Uri(site).Host);
            }
            catch (SignInRequiredException ex)
            {
                throw CliError.SignIn(ex.Message, site);
            }
        }
        else if (UxConnectionResolver.IsBrowser(connection) && !Browser.HasSession(site))
        {
            throw CliError.SignIn(
                $"The browser sign-in for {new Uri(site).Host} is missing or older than 18 hours. " +
                "Browser sign-in works on Windows only; a Microsoft sign-in or certificate connection does not expire this way.", site);
        }
        try
        {
            return UxConnectionResolver.Resolve(connection, site, Browser);
        }
        catch (FileNotFoundException ex)
        {
            throw CliError.Invalid(ex.Message, "Fix the certificate path with: cpm connection add --site <url> --sign-in certificate ...");
        }
    }
}

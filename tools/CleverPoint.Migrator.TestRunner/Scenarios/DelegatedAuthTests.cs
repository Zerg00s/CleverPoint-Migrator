using System.Text.Json;
using CleverPoint.Migrator.Core.Auth;
using CleverPoint.Migrator.Core.Csom;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// The tenant sign-in app: registration, admin consent and delegated tokens. These scenarios are
/// interactive. A browser opens and a person has to sign in, so they are not part of an unattended run.
///
///   app-setup        find or create the sign-in app in the tenant of SETUP_SITE (default: the source
///                    site) and ask for admin consent. Run it twice: the second run must reuse the app.
///   app-unattended   the opt-in app-only registration with a certificate.
///   auth-delegated   sign in through the app, call REST and CSOM, then prove a fresh provider renews
///                    silently from the cache file.
///
/// What a run learns (tenant ID to app ID) is kept in a small state file outside the repository. It
/// holds no secret: a delegated public client has none.
/// </summary>
public static class DelegatedAuthTests
{
    private static readonly string StateFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CleverPoint Migrator Tests");
    private static string StatePath => Path.Combine(StateFolder, "delegated-apps.json");
    public static string CachePath => Path.Combine(StateFolder, "msal.cache");

    private static string SetupSite => Environment.GetEnvironmentVariable("SETUP_SITE") is { Length: > 0 } s ? s : Program.Source.SiteUrl;

    public static async Task AppSetupAsync()
    {
        var host = new Uri(SetupSite).Host;
        var provisioner = new EntraAppProvisioner();
        provisioner.OnProgress += line => Console.WriteLine("  " + line);

        var admin = await provisioner.SignInAdminAsync(host);
        Program.Check("administrator signed in", admin.Length > 0, admin);

        var app = await provisioner.EnsureSignInAppAsync();
        Program.Check("sign-in app found or created", Guid.TryParse(app.AppId, out _), $"{(app.Created ? "created" : "reused")} {app.AppId}");
        SaveApp(app.TenantId, app.AppId);

        // Looking again must find the same registration, never make a second one.
        var again = await provisioner.EnsureSignInAppAsync();
        Program.Check("second lookup reuses the app", !again.Created && again.AppId == app.AppId, again.AppId);

        if (Environment.GetEnvironmentVariable("SETUP_SKIP_CONSENT") == "1")
        {
            Console.WriteLine("  consent skipped; link: " + EntraAppProvisioner.BuildAdminConsentUrl(app.TenantId, app.AppId));
            return;
        }
        await provisioner.GrantAdminConsentAsync(app.TenantId, app.AppId, app.Created);
        Program.Check("admin consent granted", true);
    }

    public static async Task AppUnattendedAsync()
    {
        var host = new Uri(SetupSite).Host;
        var provisioner = new EntraAppProvisioner();
        provisioner.OnProgress += line => Console.WriteLine("  " + line);
        await provisioner.SignInAdminAsync(host);

        var app = await provisioner.EnsureUnattendedAppAsync(Path.Combine(StateFolder, "certificates"));
        Program.Check("unattended app found or created", Guid.TryParse(app.AppId, out _), $"{(app.Created ? "created" : "reused")} {app.AppId}");
        Program.Check("certificate written", System.IO.File.Exists(app.PfxPath), Path.GetFileName(app.PfxPath));
        await provisioner.GrantAdminConsentAsync(app.TenantId, app.AppId, app.Created);

        // The grant can take a little while to reach SharePoint.
        var creds = new AppCredentials { TenantId = app.TenantId, AppId = app.AppId, CertPfxPath = app.PfxPath, CertPassword = app.PfxPassword };
        var conn = new SpConnection(SetupSite, new CertTokenProvider(creds));
        string? title = null, error = null;
        for (var attempt = 0; attempt < 12 && title is null; attempt++)
        {
            try
            {
                using var web = await conn.Rest.GetJsonAsync($"{conn.SiteUrl}/_api/web?$select=Title");
                title = web.RootElement.GetProperty("Title").GetString();
            }
            catch (Exception ex)
            {
                error = ex.Message;
                await Task.Delay(TimeSpan.FromSeconds(10));
            }
        }
        Program.Check("app-only certificate token reads the site", title != null, title ?? error);
    }

    public static async Task AuthDelegatedAsync()
    {
        var site = SetupSite;
        var host = new Uri(site).Host;
        var provider = await ProviderForAsync(host)
            ?? throw new InvalidOperationException($"No sign-in app is recorded for {host}. Run the app-setup scenario first.");

        var account = await provider.GetAccountAsync();
        if (account is null || Environment.GetEnvironmentVariable("FORCE_SIGNIN") == "1")
        {
            Console.WriteLine("  opening the browser to sign in...");
            account = await provider.SignInAsync(host);
        }
        Program.Check("signed in", account.Username.Length > 0, account.Username);

        var conn = new SpConnection(site, provider);
        using (var web = await conn.Rest.GetJsonAsync($"{conn.SiteUrl}/_api/web?$select=Title"))
            Program.Check("REST with a delegated token", true, web.RootElement.GetProperty("Title").GetString());

        using (var ctx = conn.CreateContext())
        {
            ctx.Load(ctx.Web, w => w.Title, w => w.CurrentUser.LoginName, w => w.Lists.Include(l => l.Title));
            await ctx.ExecuteWithRetryAsync();
            Program.Check("CSOM with a delegated token", ctx.Web.Lists.Count > 0,
                $"'{ctx.Web.Title}', {ctx.Web.Lists.Count} lists, as {ctx.Web.CurrentUser.LoginName}");
        }

        // What a restarted app or the command line does: no browser, only the cache file.
        var fresh = new DelegatedTokenProvider(provider.TenantId, provider.ClientId, CachePath, account.HomeAccountId);
        var token = await fresh.GetTokenAsync(host);
        Program.Check("a fresh provider renews silently from the cache", token.Length > 100);

        // The same sign-in must cover the tenant's other SharePoint hosts.
        var adminHost = host.Replace(".sharepoint.com", "-admin.sharepoint.com");
        try
        {
            var adminToken = await fresh.GetTokenAsync(adminHost);
            Program.Check("the sign-in also covers " + adminHost, adminToken.Length > 100);
        }
        catch (Exception ex)
        {
            Program.Check("the sign-in also covers " + adminHost, false, ex.Message);
        }
    }

    /// <summary>A delegated provider for the tenant that owns this host, or null when no app is recorded for it.</summary>
    public static async Task<DelegatedTokenProvider?> ProviderForAsync(string sharePointHost)
    {
        var tenantId = await TenantDiscovery.GetTenantIdAsync(sharePointHost);
        return LoadApps().TryGetValue(tenantId, out var appId) ? new DelegatedTokenProvider(tenantId, appId, CachePath) : null;
    }

    private static Dictionary<string, string> LoadApps() =>
        System.IO.File.Exists(StatePath)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(System.IO.File.ReadAllText(StatePath)) ?? new()
            : new();

    private static void SaveApp(string tenantId, string appId)
    {
        var apps = LoadApps();
        apps[tenantId] = appId;
        Directory.CreateDirectory(StateFolder);
        System.IO.File.WriteAllText(StatePath, JsonSerializer.Serialize(apps, new JsonSerializerOptions { WriteIndented = true }));
    }
}

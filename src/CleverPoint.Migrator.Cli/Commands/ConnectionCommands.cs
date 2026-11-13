using CleverPoint.Migrator.Core.Auth;
using CleverPoint.Migrator.Core.Hosting;

namespace CleverPoint.Migrator.Cli.Commands;

/// <summary>Saved connections: one per tenant host, shared with the desktop app.</summary>
public static class ConnectionCommands
{
    public static IEnumerable<Command> All() => new[]
    {
        new Command
        {
            Path = new[] { "connection", "list" },
            Summary = "List saved connections and whether each is signed in.",
            Run = _ => Task.FromResult(new Outcome(List())),
        },
        new Command
        {
            Path = new[] { "connection", "add" },
            Summary = "Save a connection to a site. Does not sign in.",
            Options = new[]
            {
                new Opt("site", "<url>", "Site URL. One connection covers every site on the same host.", Required: true),
                new Opt("sign-in", "<kind>", "microsoft (the tenant's own app, renews by itself), browser (Windows only, 18 hours) or certificate (app-only). Not needed with --file."),
                new Opt("client-id", "<guid>", "App (client) ID. Needed for microsoft and certificate."),
                new Opt("tenant-id", "<guid>", "Tenant ID. Optional: it is found from the site URL."),
                new Opt("pfx", "<file>", "Certificate file, for certificate sign-in."),
                new Opt("pfx-password-env", "<name>", "Name of an environment variable holding the certificate password. The password itself never goes on the command line."),
                new Opt("file", "<file>", "A connection file exported from another computer. Replaces --sign-in details."),
            },
            Examples = new[]
            {
                "cpm connection add --site https://contoso.sharepoint.com/sites/Finance --sign-in microsoft --client-id 11111111-2222-3333-4444-555555555555",
                "cpm connection add --site https://contoso.sharepoint.com --sign-in browser",
            },
            Notes = "A tenant with no app yet: an administrator runs `cpm connection setup --site <url>` once, which creates the app and saves the connection.",
            Run = AddAsync,
        },
        new Command
        {
            Path = new[] { "connection", "setup" },
            Summary = "Administrator: register the tenant's app, approve it, and save the connection.",
            Interactive = true,
            Options = new[]
            {
                new Opt("site", "<url>", "A site in the tenant to set up.", Required: true),
                new Opt("unattended", null, "Create the app-only app with a certificate instead. It reaches every site in the tenant, whoever runs the tool."),
            },
            Notes = "Needs a Global Administrator, Privileged Role Administrator, Cloud Application Administrator or Application Administrator.\n" +
                    "Safe to repeat: an app that already exists is found and reused.",
            Run = SetupAsync,
        },
        new Command
        {
            Path = new[] { "connection", "login" },
            Summary = "Sign in to a connection.",
            Interactive = true,
            Options = new[] { new Opt("site", "<url>", "Site URL of the connection.", Required: true) },
            Run = LoginAsync,
        },
        new Command
        {
            Path = new[] { "connection", "logout" },
            Summary = "Forget the sign-in on this computer.",
            Options = new[] { new Opt("site", "<url>", "Site URL of the connection.", Required: true) },
            Run = LogoutAsync,
        },
        new Command
        {
            Path = new[] { "connection", "test" },
            Summary = "Check that a connection can read its site right now, without prompting.",
            Options = new[] { new Opt("site", "<url>", "Site URL to read.", Required: true) },
            Run = TestAsync,
        },
        new Command
        {
            Path = new[] { "connection", "export" },
            Summary = "Write a Microsoft sign-in connection to a file for another computer. Contains no secret.",
            Options = new[]
            {
                new Opt("site", "<url>", "Site URL of the connection.", Required: true),
                new Opt("out", "<file>", "File to write.", Required: true),
            },
            Run = Export,
        },
        new Command
        {
            Path = new[] { "connection", "remove" },
            Summary = "Delete a saved connection.",
            Options = new[] { new Opt("site", "<url>", "Site URL of the connection.", Required: true) },
            Run = Remove,
        },
    };

    private static object Describe(Session session, SavedConnection c) => new
    {
        name = c.Name,
        site = c.SiteUrl,
        host = new Uri(c.SiteUrl).Host,
        signIn = Session.SignInKind(c),
        ready = session.IsReady(c),
        account = string.IsNullOrEmpty(c.AccountUpn) ? null : c.AccountUpn,
        tenantId = string.IsNullOrEmpty(c.TenantId) ? null : c.TenantId,
        clientId = string.IsNullOrEmpty(c.AppId) ? null : c.AppId,
        certificateExpires = c.CertExpiresUtc,
        lastStatus = string.IsNullOrEmpty(c.LastStatus) ? null : c.LastStatus,
    };

    private static object List()
    {
        var session = new Session();
        return new
        {
            connections = session.Settings.Connections.Select(c => Describe(session, c)).ToList(),
            settingsFolder = UxSettings.Folder,
        };
    }

    private static async Task<Outcome> AddAsync(Invocation args)
    {
        var session = new Session();
        var site = Session.SiteUrl(args.Get("site")!);
        var host = new Uri(site).Host;
        var connection = new SavedConnection { SiteUrl = site, Name = host.Split('.')[0] };

        if (args.Get("file") is { } file)
        {
            if (!File.Exists(file)) throw CliError.NotFound($"File not found: {file}");
            if (!ConnectionFile.TryParse(File.ReadAllText(file), out var fileSite, out var tenant, out var app, out var error))
                throw CliError.Invalid(error);
            if (!new Uri(fileSite).Host.Equals(host, StringComparison.OrdinalIgnoreCase))
                throw CliError.Invalid($"That file is for {new Uri(fileSite).Host}, not {host}.");
            connection.AuthMode = "AppDelegated";
            connection.TenantId = tenant;
            connection.AppId = app;
        }
        else
        {
            var kind = (args.Get("sign-in") ?? "").ToLowerInvariant();
            if (kind.Length == 0)
                throw CliError.Usage("Say how to sign in: --sign-in microsoft, browser or certificate (or --file with an exported connection).",
                    "microsoft = the tenant's own app, renews by itself. browser = Windows only, 18 hours. certificate = app-only.");
            connection.AuthMode = kind switch
            {
                "microsoft" => "AppDelegated",
                "browser" => "Browser",
                "certificate" => "AppCertificate",
                _ => throw CliError.Usage($"--sign-in '{kind}' is not known.", "Use microsoft, browser or certificate."),
            };
            if (kind != "browser")
            {
                connection.AppId = args.Get("client-id")?.Trim() ?? "";
                if (!Guid.TryParse(connection.AppId, out _))
                    throw CliError.Usage("--client-id has to be the app's GUID.",
                        "No app in this tenant yet? An administrator runs: cpm connection setup --site " + site);
                connection.TenantId = args.Get("tenant-id")?.Trim() ?? "";
            }
            if (kind == "certificate")
            {
                connection.CertPfxPath = Path.GetFullPath(args.Get("pfx") ?? throw CliError.Usage("Certificate sign-in needs --pfx <file>."));
                if (!File.Exists(connection.CertPfxPath)) throw CliError.NotFound($"Certificate file not found: {connection.CertPfxPath}");
                var variable = args.Get("pfx-password-env");
                var password = variable is null ? "" : Environment.GetEnvironmentVariable(variable)
                    ?? throw CliError.Usage($"The environment variable {variable} is not set.");
                connection.CertPasswordProtected = UxSecret.Protect(password);
            }
        }

        if (connection.AuthMode != "Browser")
        {
            // The tenant comes from the site itself, so a wrong tenant ID cannot send a sign-in elsewhere.
            var actual = await TenantDiscovery.GetTenantIdAsync(host);
            if (connection.TenantId.Length > 0 && !connection.TenantId.Equals(actual, StringComparison.OrdinalIgnoreCase))
                throw CliError.Invalid($"Tenant {connection.TenantId} does not own {host}.", "Leave --tenant-id out; it is found from the site URL.");
            connection.TenantId = actual;
        }

        var saved = Save(session, connection);
        var next = session.IsReady(saved) ? $"cpm connection test --site {site}" : $"cpm connection login --site {site}";
        return new Outcome(new { connection = Describe(session, saved), next });
    }

    /// <summary>One connection per site URL: adding it again updates it, as the desktop app does.</summary>
    private static SavedConnection Save(Session session, SavedConnection connection)
    {
        var existing = session.Settings.Connections.FirstOrDefault(c =>
            c.SiteUrl.TrimEnd('/').Equals(connection.SiteUrl, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            session.Settings.Connections.Add(connection);
            session.Settings.Save();
            return connection;
        }
        if (existing.AuthMode != connection.AuthMode || !string.Equals(existing.AppId, connection.AppId, StringComparison.OrdinalIgnoreCase))
        {
            existing.AccountUpn = "";
            existing.HomeAccountId = "";
            existing.LastStatus = "";
        }
        UxConnectionResolver.Invalidate(existing);
        existing.AuthMode = connection.AuthMode;
        existing.TenantId = connection.TenantId;
        existing.AppId = connection.AppId;
        existing.CertPfxPath = connection.CertPfxPath;
        existing.CertPasswordProtected = connection.CertPasswordProtected;
        existing.CertExpiresUtc = connection.CertExpiresUtc;
        session.Settings.Save();
        return existing;
    }

    private static async Task<Outcome> SetupAsync(Invocation args)
    {
        var session = new Session();
        var site = Session.SiteUrl(args.Get("site")!);
        var host = new Uri(site).Host;
        var provisioner = new EntraAppProvisioner();
        provisioner.OnProgress += Runner.Note;
        Runner.Note("Two Microsoft pages will open in your browser. First a sign-in for Microsoft's own setup tool: accept it and leave");
        Runner.Note("\"Consent on behalf of your organization\" unticked. Then the approval page for CleverPoint Migrator: choose Accept.");
        await provisioner.SignInAdminAsync(host);

        var connection = new SavedConnection { SiteUrl = site, Name = host.Split('.')[0] };
        bool created;
        if (args.Has("unattended"))
        {
            var app = await provisioner.EnsureUnattendedAppAsync(UxSettings.CertificateFolder);
            await provisioner.GrantAdminConsentAsync(app.TenantId, app.AppId, app.Created);
            connection.AuthMode = "AppCertificate";
            connection.TenantId = app.TenantId;
            connection.AppId = app.AppId;
            connection.CertPfxPath = app.PfxPath;
            connection.CertPasswordProtected = UxSecret.Protect(app.PfxPassword);
            connection.CertExpiresUtc = app.CertExpiresUtc;
            created = app.Created;
        }
        else
        {
            var app = await provisioner.EnsureSignInAppAsync();
            connection.AuthMode = "AppDelegated";
            connection.TenantId = app.TenantId;
            connection.AppId = app.AppId;
            created = app.Created;
            // Saved before the approval, so a declined or timed-out approval can be finished by link.
            Save(session, connection);
            try
            {
                await provisioner.GrantAdminConsentAsync(app.TenantId, app.AppId, app.Created);
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
            {
                throw new CliError("not_approved", Exit.Failed, ex.Message,
                    "The app exists and the connection is saved. An administrator can approve it later in any browser: " +
                    EntraAppProvisioner.BuildAdminConsentUrl(app.TenantId, app.AppId));
            }
        }

        var saved = Save(session, connection);
        return new Outcome(new
        {
            connection = Describe(session, saved),
            appCreated = created,
            next = args.Has("unattended")
                ? $"cpm connection test --site {site}  (a new app-only approval can take a minute or two to reach SharePoint)"
                : $"cpm connection login --site {site}",
        });
    }

    private static async Task<Outcome> LoginAsync(Invocation args)
    {
        var session = new Session();
        var site = Session.SiteUrl(args.Get("site")!);
        var connection = session.Connection(site);
        if (UxConnectionResolver.IsCert(connection))
            return new Outcome(new { connection = Describe(session, connection), message = "A certificate connection needs no sign-in." });

        if (UxConnectionResolver.IsDelegated(connection))
        {
            Runner.Note("Opening your browser to sign in...");
            var (ok, message) = await UxConnectionResolver.SignInDelegatedAsync(connection);
            if (!ok) throw new CliError("sign_in_failed", Exit.SignInRequired, message, $"Try again: cpm connection login --site {site}");
        }
        else
        {
            if (!session.Browser.Available)
                throw new CliError("not_supported", Exit.Failed, "Browser sign-in only works on Windows.",
                    "Use a Microsoft sign-in or certificate connection on this computer.");
            Runner.Note("Opening the sign-in window...");
            var (ok, message) = await session.Browser.SignInAsync(site, fresh: session.Browser.HasSession(site));
            if (!ok) throw new CliError("sign_in_failed", Exit.SignInRequired, message, $"Try again: cpm connection login --site {site}");
            connection.LastStatus = "Connected (browser)";
            connection.LastVerifiedUtc = DateTime.UtcNow;
        }
        session.Settings.Save();
        return new Outcome(new { connection = Describe(session, connection), next = $"cpm connection test --site {site}" });
    }

    private static async Task<Outcome> LogoutAsync(Invocation args)
    {
        var session = new Session();
        var site = Session.SiteUrl(args.Get("site")!);
        var connection = session.Connection(site);
        if (UxConnectionResolver.IsDelegated(connection)) await UxConnectionResolver.SignOutAsync(connection);
        else if (UxConnectionResolver.IsBrowser(connection)) session.Browser.Clear(site);
        session.Settings.Save();
        return new Outcome(new { connection = Describe(session, connection) });
    }

    private static async Task<Outcome> TestAsync(Invocation args)
    {
        var session = new Session();
        var site = Session.SiteUrl(args.Get("site")!);
        var connection = session.Connection(site);
        var live = await session.ConnectAsync(site);
        using var web = await live.Rest.GetJsonAsync($"{live.SiteUrl}/_api/web?$select=Title,Url");
        var title = web.RootElement.GetProperty("Title").GetString();
        connection.LastStatus = $"Connected ({title})";
        connection.LastVerifiedUtc = DateTime.UtcNow;
        session.Settings.Save();
        return new Outcome(new { site, title, connection = Describe(session, connection) });
    }

    private static Task<Outcome> Export(Invocation args)
    {
        var session = new Session();
        var connection = session.Connection(Session.SiteUrl(args.Get("site")!));
        if (!UxConnectionResolver.IsDelegated(connection))
            throw CliError.Invalid("Only a Microsoft sign-in connection can be exported: it is the only kind with nothing secret in it.");
        var path = Session.OutputFile(args.Get("out")!);
        File.WriteAllText(path, ConnectionFile.Serialize(connection));
        return Task.FromResult(new Outcome(new { file = path, importWith = $"cpm connection add --site {connection.SiteUrl} --file <that file>" }));
    }

    private static Task<Outcome> Remove(Invocation args)
    {
        var session = new Session();
        var connection = session.Connection(Session.SiteUrl(args.Get("site")!));
        session.Settings.Connections.Remove(connection);
        session.Settings.Save();
        return Task.FromResult(new Outcome(new { removed = connection.SiteUrl }));
    }
}

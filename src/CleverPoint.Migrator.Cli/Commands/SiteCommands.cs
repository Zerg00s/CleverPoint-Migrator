using CleverPoint.Migrator.Core.Hosting;
using CleverPoint.Migrator.Core.Operations;

namespace CleverPoint.Migrator.Cli.Commands;

/// <summary>Looking at a site before writing a job: what lists exist, what is in a folder, who the users are.</summary>
public static class SiteCommands
{
    public static IEnumerable<Command> All() => new[]
    {
        new Command
        {
            Path = new[] { "site", "lists" },
            Summary = "List the lists and libraries of a site, with item counts and paths.",
            Options = new[] { new Opt("site", "<url>", "Site URL.", Required: true) },
            Notes = "Use the `path` of a list as sourcePath or targetPath in a job file: it does not change with the site's language.",
            Run = ListsAsync,
        },
        new Command
        {
            Path = new[] { "site", "webs" },
            Summary = "List the subsites directly under a site.",
            Options = new[] { new Opt("site", "<url>", "Site URL.", Required: true) },
            Run = WebsAsync,
        },
        new Command
        {
            Path = new[] { "site", "folder" },
            Summary = "List the files and folders in one folder of a library, or the items of a list with their IDs.",
            Options = new[]
            {
                new Opt("site", "<url>", "Site URL.", Required: true),
                new Opt("list", "<path>", "Server-relative path of the list's root folder, from `cpm site lists`.", Required: true),
                new Opt("folder", "<path>", "Server-relative path of the folder. Default: the list root."),
                new Opt("limit", "<n>", "Most entries to return. Default 100."),
            },
            Run = FolderAsync,
        },
        new Command
        {
            Path = new[] { "site", "users" },
            Summary = "List the users and groups known to a site, with the sign-in names an identity mapping uses.",
            Options = new[]
            {
                new Opt("site", "<url>", "Site URL.", Required: true),
                new Opt("type", "<kind>", "Only user or only group."),
                new Opt("search", "<text>", "Only entries whose name, email or sign-in name contains this text."),
                new Opt("limit", "<n>", "Most entries to return. Default 200."),
            },
            Run = UsersAsync,
        },
        new Command
        {
            Path = new[] { "mapping", "template" },
            Summary = "Write an identity mapping CSV listing the source site's users and groups, prefilled where the target has the same account.",
            Options = new[]
            {
                new Opt("source", "<url>", "Source site URL.", Required: true),
                new Opt("target", "<url>", "Target site URL.", Required: true),
                new Opt("out", "<file>", "CSV file to write.", Required: true),
            },
            Notes = "Columns: Type,Source,Target. Fill in the empty Target cells, then name the file in the job as identityMapping.csv.",
            Run = MappingTemplateAsync,
        },
    };

    private static async Task<Outcome> ListsAsync(Invocation args)
    {
        var session = new Session();
        var site = Session.SiteUrl(args.Get("site")!);
        var lists = await session.Sites.GetListsAsync(await session.ConnectAsync(site), useCache: false);
        return new Outcome(new
        {
            site,
            lists = lists.Select(l => new
            {
                title = l.Title,
                path = l.ServerRelativeUrl,
                type = l.IsLibrary ? "library" : "list",
                items = l.ItemCount,
                sitePages = l.BaseTemplate == ListLocator.SitePagesTemplate ? true : (bool?)null,
            }).ToList(),
        });
    }

    private static async Task<Outcome> WebsAsync(Invocation args)
    {
        var session = new Session();
        var site = Session.SiteUrl(args.Get("site")!);
        var webs = await session.Sites.GetSubwebsAsync(await session.ConnectAsync(site), useCache: false);
        return new Outcome(new { site, webs = webs.Select(w => new { title = w.Title, url = w.Url }).ToList() });
    }

    private static async Task<Outcome> FolderAsync(Invocation args)
    {
        var session = new Session();
        var site = Session.SiteUrl(args.Get("site")!);
        var list = args.Get("list")!;
        var folder = args.Get("folder") ?? list;
        var limit = Math.Clamp(args.Int("limit", 100), 1, 5000);
        var connection = await session.ConnectAsync(site);

        // The items of a plain list have no useful file name; what a job needs is their ID and title.
        static string Norm(string path) => Uri.UnescapeDataString(path).Trim().TrimEnd('/').ToLowerInvariant();
        var known = (await session.Sites.GetListsAsync(connection, useCache: false)).FirstOrDefault(l => Norm(l.ServerRelativeUrl) == Norm(list));
        if (known is { IsLibrary: false } && Norm(folder) == Norm(list))
        {
            var items = await session.Sites.GetListItemsAsync(connection, known);
            return new Outcome(new
            {
                list = known.ServerRelativeUrl,
                total = items.Count,
                truncated = items.Count > limit ? true : (bool?)null,
                entries = items.Take(limit).Select(e => new
                {
                    name = e.Name,
                    type = "item",
                    itemId = e.ItemId,
                    modified = e.ModifiedUtc,
                    modifiedBy = string.IsNullOrEmpty(e.ModifiedBy) ? null : e.ModifiedBy,
                    created = e.CreatedUtc,
                    createdBy = string.IsNullOrEmpty(e.CreatedBy) ? null : e.CreatedBy,
                }).ToList(),
            });
        }

        var entries = await session.Sites.GetFolderAsync(connection, folder, list);
        // An empty answer is also what a folder that does not exist gives. Tell the two apart, so a
        // mistyped path is an error and not "this folder is empty".
        if (entries.Count == 0)
        {
            var escaped = Uri.EscapeDataString(folder.Replace("'", "''"));
            using var probe = await connection.Rest.GetJsonAsync(
                $"{connection.SiteUrl}/_api/web/GetFolderByServerRelativePath(decodedurl='{escaped}')?$select=Exists");
            if (!probe.RootElement.TryGetProperty("Exists", out var exists) || !exists.GetBoolean())
                throw CliError.NotFound($"There is no folder {folder} on that site.", $"List what the site has with: cpm site lists --site {site}");
        }
        return new Outcome(new
        {
            folder,
            total = entries.Count,
            truncated = entries.Count > limit ? true : (bool?)null,
            entries = entries.Take(limit).Select(e => new
            {
                name = e.Name,
                path = e.ServerRelativeUrl,
                type = e.IsFolder ? "folder" : "file",
                size = e.IsFolder ? (long?)null : e.Size,
                itemId = e.ItemId == 0 ? (int?)null : e.ItemId,
                modified = e.ModifiedUtc,
                modifiedBy = string.IsNullOrEmpty(e.ModifiedBy) ? null : e.ModifiedBy,
                created = e.CreatedUtc,
                createdBy = string.IsNullOrEmpty(e.CreatedBy) ? null : e.CreatedBy,
            }).ToList(),
        });
    }

    private static async Task<Outcome> UsersAsync(Invocation args)
    {
        var session = new Session();
        var site = Session.SiteUrl(args.Get("site")!);
        var limit = Math.Clamp(args.Int("limit", 200), 1, 5000);
        var all = await session.Sites.GetPrincipalsAsync(await session.ConnectAsync(site), useCache: false);
        var type = args.Get("type")?.ToLowerInvariant();
        if (type is not (null or "user" or "group")) throw CliError.Usage($"--type '{type}' is not known.", "Use user or group.");
        var search = args.Get("search");
        var principals = all
            .Where(p => type is null || (type == "group") == p.IsGroup)
            .Where(p => search is null || p.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                        || p.Email.Contains(search, StringComparison.OrdinalIgnoreCase) || p.Login.Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return new Outcome(new
        {
            site,
            users = all.Count(p => !p.IsGroup),
            groups = all.Count(p => p.IsGroup),
            total = principals.Count,
            truncated = principals.Count > limit ? true : (bool?)null,
            principals = principals.Take(limit).Select(p => new
            {
                name = p.Title,
                type = p.IsGroup ? "group" : "user",
                email = p.Email.Length == 0 ? null : p.Email,
                signIn = p.Login,
            }).ToList(),
        });
    }

    private static async Task<Outcome> MappingTemplateAsync(Invocation args)
    {
        var session = new Session();
        var source = Session.SiteUrl(args.Get("source")!);
        var target = Session.SiteUrl(args.Get("target")!);
        var path = Session.OutputFile(args.Get("out")!);
        var sourcePrincipals = await session.Sites.GetPrincipalsAsync(await session.ConnectAsync(source), useCache: false);
        var targetPrincipals = await session.Sites.GetPrincipalsAsync(await session.ConnectAsync(target), useCache: false);

        // An account is "the same" when the target has that email, or failing that the same sign-in
        // name (which is all an account without a mailbox has), or for groups the same name. The CSV
        // keys a user by email when there is one, else by sign-in name.
        var targetByEmail = targetPrincipals.Where(p => !p.IsGroup && p.Email.Length > 0)
            .GroupBy(p => p.Email, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Login, StringComparer.OrdinalIgnoreCase);
        var targetLogins = targetPrincipals.Where(p => !p.IsGroup).Select(p => p.Login).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targetGroups = targetPrincipals.Where(p => p.IsGroup).Select(p => p.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var detected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var users = sourcePrincipals.Where(p => !p.IsGroup).Select(p => (p.Login, p.Email, p.Title)).ToList();
        foreach (var user in users)
        {
            var key = user.Email.Length > 0 ? user.Email : user.Login;
            if (user.Email.Length > 0 && targetByEmail.TryGetValue(user.Email, out var sameEmail)) detected[key] = sameEmail;
            else if (targetLogins.Contains(user.Login)) detected[key] = user.Login;
        }
        var groups = sourcePrincipals.Where(p => p.IsGroup).Select(p => p.Title).ToList();
        foreach (var group in groups.Where(targetGroups.Contains)) detected[group] = group;

        UserMappingStore.ExportTemplate(path, users, groups, detected);
        return new Outcome(new
        {
            file = path,
            users = users.Count,
            groups = groups.Count,
            prefilled = detected.Count,
            toFillIn = users.Count + groups.Count - detected.Count,
        });
    }
}

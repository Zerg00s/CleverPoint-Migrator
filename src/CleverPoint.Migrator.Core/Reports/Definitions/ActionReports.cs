using CleverPoint.Migrator.Core.Csom;
using CleverPoint.Migrator.Core.Http;
using Microsoft.Online.SharePoint.TenantAdministration;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.Core.Reports.Definitions;

// Actions change sites. Each of them works the same way: without "apply" it reads the current state
// and writes what it would do; with "apply" it does it and writes what happened. The preview and the
// real run produce the same table, so the first can be checked before the second is started.

/// <summary>Locks or unlocks site collections.</summary>
public sealed class SiteLockAction : ReportDefinition
{
    public override string Id => "site-lock";
    public override string Title => "Lock or unlock sites";
    public override string Group => "Actions";
    public override string Summary => "Set sites to read-only, to no access, or back to unlocked. Typical at cutover: lock the source sites once their content has been migrated.";
    public override bool NeedsTenantAdmin => true;
    public override bool ChangesData => true;

    public override IReadOnlyList<ReportOption> Options => new[]
    {
        new ReportOption("state", "text", "", "Required. ReadOnly = people can open the site but not change it. NoAccess = nobody can open it. Unlock = back to normal.",
            new[] { "ReadOnly", "NoAccess", "Unlock" }),
    };

    public override IReadOnlyList<ReportTable> Tables => new[]
    {
        Table("sites", "Sites", Text("action"), Text("stateBefore"), Text("stateAfter"), IfAny(Text("error"))),
    };

    private static string? Wanted(ReportOptions options) => options.Text("state").Trim().ToLowerInvariant() switch
    {
        "readonly" or "read-only" => "ReadOnly",
        "noaccess" or "no-access" => "NoAccess",
        "unlock" or "unlocked" or "active" => "Unlock",
        _ => null,
    };

    public override IEnumerable<string> CheckOptions(ReportOptions options)
    {
        if (Wanted(options) is not null) yield break;
        var given = options.Text("state").Trim();
        yield return given.Length == 0
            ? "Say what to set the sites to: the setting state takes ReadOnly, NoAccess or Unlock."
            : $"'{given}' is not a lock state. Use ReadOnly, NoAccess or Unlock.";
    }

    public override async Task RunAsync(ReportContext context)
    {
        var wanted = Wanted(context.Options) ?? throw new InvalidOperationException(CheckOptions(context.Options).First());
        var admin = await context.Connect(ReportSpecs.AdminUrl(context.Site.Url));
        using var csom = admin.CreateContext();
        var tenant = new Tenant(csom);
        var site = tenant.GetSitePropertiesByUrl(context.Site.Url, includeDetail: false);
        csom.Load(site, s => s.LockState, s => s.Url);
        await csom.ExecuteWithRetryAsync();

        var before = site.LockState ?? "";
        var row = new Dictionary<string, object?> { ["stateBefore"] = TenantSites.LockStateName(before) };
        if (string.Equals(before, wanted, StringComparison.OrdinalIgnoreCase))
        {
            row["action"] = "Already " + Describe(wanted);
            row["stateAfter"] = TenantSites.LockStateName(before);
            context.Count("unchanged");
        }
        else if (!context.Apply)
        {
            row["action"] = "Would set to " + Describe(wanted);
            row["stateAfter"] = TenantSites.LockStateName(wanted);
            context.Count("wouldChange");
        }
        else
        {
            site.LockState = wanted;
            var operation = site.Update();
            csom.Load(operation, o => o.IsComplete);
            await csom.ExecuteWithRetryAsync();
            // The change is queued; wait for it so the row says what is true, within reason.
            for (var attempt = 0; attempt < 30 && !operation.IsComplete; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), context.Cancel);
                operation.RefreshLoad();
                await csom.ExecuteWithRetryAsync();
            }
            row["action"] = operation.IsComplete ? "Set to " + Describe(wanted) : "Requested; SharePoint has not confirmed it yet";
            row["stateAfter"] = TenantSites.LockStateName(wanted);
            context.Count("changed");
        }
        context.Write("sites", row);
    }

    private static string Describe(string state) => state switch
    {
        "ReadOnly" => "read-only",
        "NoAccess" => "no access",
        _ => "unlocked",
    };
}

/// <summary>Adds or removes site collection administrators.</summary>
public sealed class SiteAdminsAction : ReportDefinition
{
    public override string Id => "site-admins";
    public override string Title => "Site collection administrators";
    public override string Group => "Actions";
    public override string Summary => "Add accounts as site collection administrator on many sites, or remove them. Useful to give a migration account access before a run and take it away after.";
    public override bool NeedsTenantAdmin => true;
    public override bool ChangesData => true;

    public override IReadOnlyList<ReportOption> Options => new[]
    {
        new ReportOption("add", "list", new List<string>(), "People (user@contoso.com) or security groups to make site collection administrator.", Accounts: true),
        new ReportOption("remove", "list", new List<string>(), "People or security groups to remove as site collection administrator.", Accounts: true),
    };

    public override IReadOnlyList<ReportTable> Tables => new[]
    {
        Table("changes", "Changes", Text("account"), Text("action"), IfAny(Text("error"))),
    };

    public override IEnumerable<string> CheckOptions(ReportOptions options)
    {
        var add = Clean(options.List("add"));
        var remove = Clean(options.List("remove"));
        if (add.Count + remove.Count == 0) yield return "Name at least one account in the settings add or remove.";
        if (add.Intersect(remove, StringComparer.OrdinalIgnoreCase).FirstOrDefault() is { } both) yield return $"'{both}' is in both add and remove.";
    }

    public override async Task RunAsync(ReportContext context)
    {
        var add = Clean(context.Options.List("add"));
        var remove = Clean(context.Options.List("remove"));
        if (CheckOptions(context.Options).FirstOrDefault() is { } problem) throw new InvalidOperationException(problem);

        // Who is administrator now, from the site itself. Sign-in names carry a claims prefix, so the
        // account asked for is looked for at the end of the name and in the email.
        var current = await context.Connection.Rest.GetAllAsync(
            $"{context.Connection.SiteUrl}/_api/web/siteusers?$filter=IsSiteAdmin eq true&$select=LoginName,Email", context.Cancel);
        bool IsAdmin(string account) => current.Any(u =>
            u.Str("Email").Equals(account, StringComparison.OrdinalIgnoreCase)
            || u.Str("LoginName").EndsWith("|" + account, StringComparison.OrdinalIgnoreCase)
            || u.Str("LoginName").Equals(account, StringComparison.OrdinalIgnoreCase));

        Tenant? tenant = null;
        ClientContext? csom = null;
        try
        {
            foreach (var (account, makeAdmin) in add.Select(a => (a, true)).Concat(remove.Select(r => (r, false))))
            {
                context.Cancel.ThrowIfCancellationRequested();
                var row = new Dictionary<string, object?> { ["account"] = account };
                var isAdmin = IsAdmin(account);
                if (isAdmin == makeAdmin)
                {
                    row["action"] = makeAdmin ? "Already an administrator" : "Was not an administrator";
                    context.Count("unchanged");
                }
                else if (!context.Apply)
                {
                    row["action"] = makeAdmin ? "Would add" : "Would remove";
                    context.Count("wouldChange");
                }
                else
                {
                    try
                    {
                        if (tenant is null)
                        {
                            csom = (await context.Connect(ReportSpecs.AdminUrl(context.Site.Url))).CreateContext();
                            tenant = new Tenant(csom);
                        }
                        tenant.SetSiteAdmin(context.Site.Url, account, makeAdmin);
                        await csom!.ExecuteWithRetryAsync();
                        row["action"] = makeAdmin ? "Added" : "Removed";
                        context.Count("changed");
                    }
                    catch (ServerException ex)
                    {
                        // One account that does not exist must not stop the others.
                        row["action"] = "Failed";
                        row["error"] = ex.Message;
                        context.Count("failed");
                    }
                }
                context.Write("changes", row);
            }
        }
        finally
        {
            csom?.Dispose();
        }
    }

    private static List<string> Clean(IReadOnlyList<string> accounts) =>
        accounts.Select(a => a.Trim()).Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>Copies the two navigation settings of a site that a content copy does not carry.</summary>
public sealed class NavSyncAction : ReportDefinition
{
    public override string Id => "nav-sync";
    public override string Title => "Navigation settings";
    public override string Group => "Actions";
    public override string Summary => "Compare the navigation layout of source and target sites (horizontal or vertical, mega menu or cascading) and make the target match the source.";
    public override ReportScope Scope => ReportScope.SitePairs;
    public override bool ChangesData => true;

    public override IReadOnlyList<ReportTable> Tables => new[]
    {
        Table("sites", "Sites",
            Text("action"), Text("sourceNavigation"), Text("targetNavigation"), Text("sourceMenu"), Text("targetMenu"), IfAny(Text("error"))),
    };

    public override async Task RunAsync(ReportContext context)
    {
        var target = context.Target ?? throw new InvalidOperationException("This action needs a target site.");
        const string select = "$select=HorizontalQuickLaunch,MegaMenuEnabled";
        using var source = await context.Connection.Rest.GetJsonAsync($"{context.Connection.SiteUrl}/_api/web?{select}");
        using var there = await target.Rest.GetJsonAsync($"{target.SiteUrl}/_api/web?{select}");
        bool sourceHorizontal = source.RootElement.Bool("HorizontalQuickLaunch"), sourceMega = source.RootElement.Bool("MegaMenuEnabled");
        bool targetHorizontal = there.RootElement.Bool("HorizontalQuickLaunch"), targetMega = there.RootElement.Bool("MegaMenuEnabled");

        var row = new Dictionary<string, object?>
        {
            ["sourceNavigation"] = sourceHorizontal ? "Horizontal" : "Vertical",
            ["targetNavigation"] = targetHorizontal ? "Horizontal" : "Vertical",
            ["sourceMenu"] = sourceMega ? "Mega menu" : "Cascading",
            ["targetMenu"] = targetMega ? "Mega menu" : "Cascading",
        };
        if (sourceHorizontal == targetHorizontal && sourceMega == targetMega)
        {
            row["action"] = "Already the same";
            context.Count("unchanged");
        }
        else if (!context.Apply)
        {
            row["action"] = "Would change the target";
            context.Count("wouldChange");
        }
        else
        {
            var body = "{\"__metadata\":{\"type\":\"SP.Web\"},\"HorizontalQuickLaunch\":" + (sourceHorizontal ? "true" : "false")
                       + ",\"MegaMenuEnabled\":" + (sourceMega ? "true" : "false") + "}";
            await target.Rest.SendAsync(HttpMethod.Post, $"{target.SiteUrl}/_api/web", body,
                new Dictionary<string, string> { ["X-HTTP-Method"] = "MERGE", ["IF-MATCH"] = "*" }, "application/json;odata=verbose");
            row["action"] = "Changed the target";
            row["targetNavigation"] = row["sourceNavigation"];
            row["targetMenu"] = row["sourceMenu"];
            context.Count("changed");
        }
        context.Write("sites", row);
    }
}

/// <summary>Marks notebooks that open as plain folders as notebooks again.</summary>
public sealed class FixNotebooksAction : ReportDefinition
{
    public override string Id => "fix-notebooks";
    public override string Title => "Repair OneNote notebooks";
    public override string Group => "Actions";
    public override string Summary => "Find OneNote notebooks that open as a plain folder and mark them as notebooks again. Changes one property of the folder; the pages inside are not touched.";
    public override bool ChangesData => true;

    public override IReadOnlyList<ReportOption> Options => new[]
    {
        new ReportOption("includeSubsites", "bool", false, "Also scan every subsite of each site."),
    };

    public override IReadOnlyList<ReportTable> Tables => new[]
    {
        Table("notebooks", "Notebooks", Text("webUrl"), Text("library"), Text("notebook"), Text("action"), Text("folderUrl"), IfAny(Text("error"))),
    };

    public override async Task RunAsync(ReportContext context)
    {
        var authority = new Uri(context.Connection.SiteUrl).GetLeftPart(UriPartial.Authority);
        foreach (var web in await WebWalker.WebsAsync(context, context.Options.Bool("includeSubsites")))
        {
            foreach (var library in await FileWalker.LibrariesAsync(context.Connection, web, includeHidden: false, context.Cancel))
            {
                if (library.Items == 0) continue;
                context.Phase($"{library.Title}, {library.Items:N0} items");
                List<FoundNotebook> broken;
                try
                {
                    broken = (await NotebookFinder.FindAsync(context.Connection, web, library.Id, library.Title, library.RootFolder, context.Cancel,
                            FileWalker.Progress(context, library.Title, library.Items)))
                        .Where(n => n.Broken).ToList();
                }
                catch (Exception ex) when (Gone.Is(ex))
                {
                    context.Count(Gone.Counter);
                    continue;
                }
                if (broken.Count == 0) continue;

                using var csom = context.Apply ? context.Connection.ForWeb(web).CreateContext() : null;
                foreach (var notebook in broken)
                {
                    context.Cancel.ThrowIfCancellationRequested();
                    var row = new Dictionary<string, object?>
                    {
                        ["webUrl"] = web, ["library"] = notebook.Library, ["notebook"] = notebook.Name, ["folderUrl"] = authority + notebook.Path,
                    };
                    if (csom is null)
                    {
                        row["action"] = "Would repair";
                        context.Count("wouldChange");
                    }
                    else
                    {
                        try
                        {
                            // The same write the copy engine makes for a notebook it has just copied. The
                            // folder's "modified" and "modified by" are read first and written back with it,
                            // so the repair does not show up as a change to the notebook.
                            var item = csom.Web.Lists.GetById(Guid.Parse(notebook.LibraryId)).GetItemById(notebook.ItemId);
                            csom.Load(item, i => i["Modified"], i => i["Editor"]);
                            await csom.ExecuteWithRetryAsync();
                            var modified = item["Modified"];
                            var editor = item["Editor"];
                            item["HTML_x0020_File_x0020_Type"] = FoundNotebook.NotebookType;
                            item["Modified"] = modified;
                            if (editor is not null) item["Editor"] = editor;
                            item.UpdateOverwriteVersion();
                            await csom.ExecuteWithRetryAsync();
                            row["action"] = "Repaired";
                            context.Count("changed");
                        }
                        catch (Exception ex) when (ex is ServerException or SpRestException)
                        {
                            row["action"] = "Failed";
                            row["error"] = ex.Message;
                            context.Count("failed");
                        }
                    }
                    context.Write("notebooks", row);
                }
            }
        }
    }
}

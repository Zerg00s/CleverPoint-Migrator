using System.Text.Json;
using CleverPoint.Migrator.Core.Csom;
using CleverPoint.Migrator.Core.History;
using CleverPoint.Migrator.Core.Hosting;
using CleverPoint.Migrator.Core.Reports;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// The report actions that change sites, run for real, on things made for the purpose only:
///
///   - fix-notebooks on a library this test creates and deletes again;
///   - nav-sync on one of the language test subsites (lang-tgt-en), put back afterwards.
///
/// Each action is run as a preview first (nothing may change), then applied (the change has to be
/// there), then checked again. site-lock and site-admins change whole site collections and are not
/// run here; their previews are covered by the command-line tests.
/// </summary>
public static class ReportActionTests
{
    public static async Task RunAsync()
    {
        var site = Program.TestSite ?? await TestAssets.EnsureTestSiteAsync(Program.Source);
        Program.TestSite = site;

        // Runs are recorded in a settings folder of their own, not in the real one.
        var home = Path.Combine(Path.GetTempPath(), "cpm-report-actions-" + Guid.NewGuid().ToString("N")[..8]);
        var previousHome = Environment.GetEnvironmentVariable("CPM_HOME");
        Environment.SetEnvironmentVariable("CPM_HOME", home);
        Directory.CreateDirectory(home);
        try
        {
            await NotebooksAsync(site);
            await NavigationAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("CPM_HOME", previousHome);
            try { Directory.Delete(home, recursive: true); } catch { /* a leftover temp folder is harmless */ }
        }
    }

    /// <summary>Runs a report through the same runner the command line and the app use.</summary>
    private static async Task<(string Status, ReportStore Store, Dictionary<string, long> Summary)> RunAsync(ReportSpec spec)
    {
        var id = ReportRunner.NewRunId();
        using (var history = new HistoryStore(UxSettings.HistoryDbPath))
            history.CreateReportRun(new ReportRun
            {
                Id = id, Report = spec.Report, Name = spec.Report, SpecJson = JsonSerializer.Serialize(spec, ReportSpecs.Json), Folder = ReportStore.FolderFor(id),
            });
        var status = await ReportRunner.ExecuteAsync(id, url => Task.FromResult(Program.Source.ForWeb(url.TrimEnd('/'))), _ => { });
        var store = new ReportStore(ReportStore.FolderFor(id));
        return (status, store, store.Summary());
    }

    private static string Cell(Dictionary<string, JsonElement> row, string name) => row.TryGetValue(name, out var value) ? ReportStore.CellText(value) : "";

    // ---- fix-notebooks ---------------------------------------------------------------------------

    private static async Task NotebooksAsync(SpConnection site)
    {
        var title = "CliT-Notebooks-" + Guid.NewGuid().ToString("N")[..6];
        Guid listId;
        string root;
        using (var ctx = site.CreateContext())
        {
            var library = ctx.Web.Lists.Add(new ListCreationInformation { Title = title, TemplateType = (int)ListTemplateType.DocumentLibrary });
            ctx.Load(library, l => l.Id, l => l.RootFolder.ServerRelativeUrl);
            await ctx.ExecuteWithRetryAsync();
            listId = library.Id;
            root = library.RootFolder.ServerRelativeUrl;

            // A notebook the way a file copy leaves it: the right files in the right folders, but the
            // folder itself is an ordinary folder. Inside it a section group, which has a table of
            // contents of its own and must not be taken for a second notebook.
            foreach (var folder in new[] { "Team Notebook", "Team Notebook/Section Group", "Plain Folder" })
            {
                var parent = folder.Contains('/') ? $"{root}/{folder[..folder.LastIndexOf('/')]}" : root;
                ctx.Web.GetFolderByServerRelativeUrl(parent).Folders.Add(folder[(folder.LastIndexOf('/') + 1)..]);
                await ctx.ExecuteWithRetryAsync();
            }
            foreach (var file in new[]
                     {
                         "Team Notebook/Open Notebook.onetoc2", "Team Notebook/Meetings.one", "Team Notebook/Section Group/Open Notebook.onetoc2",
                         "Team Notebook/Section Group/Ideas.one", "Plain Folder/notes.txt",
                     })
            {
                var slash = file.LastIndexOf('/');
                ctx.Web.GetFolderByServerRelativeUrl($"{root}/{file[..slash]}").Files.Add(new FileCreationInformation
                {
                    Url = file[(slash + 1)..], Content = new byte[] { 1, 2, 3, 4 }, Overwrite = true,
                });
                await ctx.ExecuteWithRetryAsync();
            }
        }

        try
        {
            var siteUrl = site.SiteUrl;
            async Task<List<Dictionary<string, JsonElement>>> MineAsync(string report, bool apply = false)
            {
                var (status, store, _) = await RunAsync(new ReportSpec { Report = report, Sites = { siteUrl }, Apply = apply });
                Program.Check($"report-actions: {report}{(apply ? " (apply)" : "")} completes", status == "Completed", status);
                return store.ReadRows("notebooks").Where(r => Cell(r, "library") == title).ToList();
            }

            var found = await MineAsync("broken-notebooks");
            Program.Check("report-actions: the notebook is found once; its section group and the plain folder are not notebooks",
                found.Count == 1 && Cell(found[0], "notebook") == "Team Notebook", string.Join(", ", found.Select(r => Cell(r, "notebook"))));
            Program.Check("report-actions: a notebook that is an ordinary folder is reported as broken",
                found.Count == 1 && Cell(found[0], "state") == "Broken", found.Count == 1 ? Cell(found[0], "state") : "not found");
            var modifiedBefore = await FolderModifiedAsync(site, $"{root}/Team Notebook");

            var preview = await MineAsync("fix-notebooks");
            Program.Check("report-actions: the preview says it would repair it",
                preview.Count == 1 && Cell(preview[0], "action") == "Would repair", string.Join(", ", preview.Select(r => Cell(r, "action"))));
            found = await MineAsync("broken-notebooks");
            Program.Check("report-actions: the preview changed nothing", found.Count == 1 && Cell(found[0], "state") == "Broken");

            var applied = await MineAsync("fix-notebooks", apply: true);
            Program.Check("report-actions: applying repairs it", applied.Count == 1 && Cell(applied[0], "action") == "Repaired",
                string.Join(", ", applied.Select(r => Cell(r, "action") + " " + Cell(r, "error"))));
            found = await MineAsync("broken-notebooks");
            Program.Check("report-actions: afterwards it is a healthy notebook",
                found.Count == 1 && Cell(found[0], "state") == "Healthy" && Cell(found[0], "markedAs") == "OneNote.Notebook", found.Count == 1 ? Cell(found[0], "state") : "gone");
            var modifiedAfter = await FolderModifiedAsync(site, $"{root}/Team Notebook");
            Program.Check("report-actions: the repair left the folder's modified date and modified-by alone",
                modifiedAfter == modifiedBefore, $"{modifiedBefore} -> {modifiedAfter}");
            var again = await MineAsync("fix-notebooks", apply: true);
            Program.Check("report-actions: a second run finds nothing left to repair", again.Count == 0, $"{again.Count} row(s)");
        }
        finally
        {
            using var ctx = site.CreateContext();
            ctx.Web.Lists.GetById(listId).DeleteObject();
            await ctx.ExecuteWithRetryAsync();
        }
    }

    /// <summary>When a folder was last modified and by whom, as one comparable value.</summary>
    private static async Task<(DateTime When, int By)> FolderModifiedAsync(SpConnection site, string serverRelativeUrl)
    {
        using var ctx = site.CreateContext();
        var item = ctx.Web.GetFolderByServerRelativeUrl(serverRelativeUrl).ListItemAllFields;
        ctx.Load(item, i => i["Modified"], i => i["Editor"]);
        await ctx.ExecuteWithRetryAsync();
        return ((DateTime)item["Modified"], (item["Editor"] as FieldUserValue)?.LookupId ?? 0);
    }

    // ---- nav-sync --------------------------------------------------------------------------------

    private static async Task NavigationAsync()
    {
        // The site collection's top site (horizontal navigation, mega menu) as the source to copy from,
        // one language test subsite as the target, and its twin as the way back: the two were made alike.
        var top = Program.Source.SiteUrl.TrimEnd('/');
        var target = top + "/lang-tgt-en";
        var twin = top + "/lang-src-en";

        async Task<string> SyncAsync(string source, bool apply)
        {
            var (status, store, _) = await RunAsync(new ReportSpec
            {
                Report = "nav-sync", Pairs = { new ReportPair { Source = source, Target = target } }, Apply = apply,
            });
            var row = store.ReadRows("sites").FirstOrDefault();
            return status != "Completed" ? "run " + status + ": " + string.Join("; ", store.Sites().Select(s => s.Error)) : row is null ? "no row" : Cell(row, "action");
        }

        var start = await SyncAsync(twin, apply: false);
        if (start.StartsWith("run "))
        {
            Console.WriteLine($"  report-actions: no language test subsites here, navigation not tested ({start})");
            return;
        }
        if (start != "Already the same" || await SyncAsync(top, apply: false) != "Would change the target")
        {
            Console.WriteLine("  report-actions: the test subsites do not differ from the top site as expected, navigation not tested");
            return;
        }

        Program.Check("report-actions: nav-sync preview says the target would change, and changes nothing",
            await SyncAsync(top, apply: false) == "Would change the target");
        try
        {
            var applied = await SyncAsync(top, apply: true);
            Program.Check("report-actions: nav-sync applied changes the target", applied == "Changed the target", applied);
            var after = await SyncAsync(top, apply: false);
            Program.Check("report-actions: afterwards source and target navigation are the same", after == "Already the same", after);
        }
        finally
        {
            // Put the test subsite back the way its twin still is.
            var restored = await SyncAsync(twin, apply: true);
            var check = await SyncAsync(twin, apply: false);
            Program.Check("report-actions: the test subsite's navigation was put back", restored is "Changed the target" or "Already the same" && check == "Already the same",
                $"{restored} / {check}");
        }
    }
}

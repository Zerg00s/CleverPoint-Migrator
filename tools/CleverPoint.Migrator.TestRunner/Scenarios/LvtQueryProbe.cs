using CleverPoint.Migrator.Core.Csom;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Maps the List View Threshold hazard surface: runs each query SHAPE the engine and browser use against
/// a library that is already far over the threshold ("Library 3", 15,000 items flat in its root folder),
/// and reports which ones throw. This pins down exactly which call produces
/// "The attempted operation is prohibited because it exceeds the list view threshold"
/// instead of guessing from reading code.
///
/// Read the OK/THROWS table in the output: anything the engine does that THROWS is a bug to fix.
/// </summary>
public static class LvtQueryProbe
{
    private const string Site = "https://gocleverpointcom.sharepoint.com/sites/DemoLargeSite";
    private const string BigLib = "Library 3";

    public static async Task RunAsync()
    {
        var conn = new SpConnection(Site, new Core.Auth.CertTokenProvider(Program.SourceCreds));
        using var ctx = conn.CreateContext();
        var list = ctx.Web.Lists.GetByTitle(BigLib);
        ctx.Load(list, l => l.ItemCount, l => l.RootFolder.ServerRelativeUrl, l => l.RootFolder.ItemCount);
        await ctx.ExecuteQueryAsync();
        var root = list.RootFolder.ServerRelativeUrl;
        Console.WriteLine($"  {BigLib}: {list.ItemCount:N0} items, root folder holds {list.RootFolder.ItemCount:N0} direct children");
        Console.WriteLine();

        // --- shapes the ENGINE uses (these MUST be safe) ---
        await ProbeAsync("ENGINE LoadAllItemsAsync  (paged RecursiveAll)", true, async () =>
            await PagedAsync(ctx, list, "<View Scope='RecursiveAll'><RowLimit Paged='TRUE'>200</RowLimit></View>", null));

        await ProbeAsync("ENGINE ScanFolderAsync    (paged RecursiveAll + folder)", true, async () =>
            await PagedAsync(ctx, list, "<View Scope='RecursiveAll'><RowLimit Paged='TRUE'>200</RowLimit></View>", root));

        // The two shapes the engine USED to send. Kept as documentation + teeth: they must still throw,
        // otherwise the fixes below prove nothing.
        await ProbeAsync("OLD    MaxItemIdAsync     (no RecursiveAll)", false, async () =>
        {
            var q = new CamlQuery { ViewXml = "<View><Query><OrderBy><FieldRef Name='ID' Ascending='FALSE'/></OrderBy></Query><RowLimit>1</RowLimit></View>" };
            var top = list.GetItems(q);
            ctx.Load(top, t => t.Include(i => i.Id));
            await ctx.ExecuteQueryAsync();
            return top.Count;
        });

        await ProbeAsync("OLD    ReconcileFailedBatch (no RowLimit)", false, async () =>
        {
            var q = new CamlQuery
            {
                ViewXml = "<View><Query><Where><Gt><FieldRef Name='ID'/><Value Type='Counter'>0</Value></Gt></Where>"
                          + "<OrderBy><FieldRef Name='ID' Ascending='TRUE'/></OrderBy></Query></View>",
            };
            var items = list.GetItems(q);
            ctx.Load(items, c => c.Include(i => i.Id));
            await ctx.ExecuteQueryAsync();
            return items.Count;
        });

        // The shapes the engine sends NOW.
        await ProbeAsync("ENGINE MaxItemIdAsync     (RecursiveAll + RowLimit 1)", true, async () =>
        {
            var q = new CamlQuery
            {
                ViewXml = "<View Scope='RecursiveAll'><Query><OrderBy><FieldRef Name='ID' Ascending='FALSE'/></OrderBy>"
                          + "</Query><RowLimit>1</RowLimit></View>",
            };
            var top = list.GetItems(q);
            ctx.Load(top, t => t.Include(i => i.Id));
            await ctx.ExecuteQueryAsync();
            return top.AsEnumerable().Select(i => i.Id).DefaultIfEmpty(0).Max();
        });

        // A <Where> that MATCHES >5,000 rows throws even with a RowLimit -- "ID > 0" is the worst case, and
        // is exactly what a zero/unknown baseline degrades into. This documents why the engine has no Where.
        await ProbeAsync("OLD    Reconcile w/ Where ID>0 + RecursiveAll + RowLimit", false, async () =>
        {
            var q = new CamlQuery
            {
                ViewXml = "<View Scope='RecursiveAll'><Query><Where><Gt><FieldRef Name='ID'/>"
                          + "<Value Type='Counter'>0</Value></Gt></Where>"
                          + "<OrderBy><FieldRef Name='ID' Ascending='TRUE'/></OrderBy></Query>"
                          + "<RowLimit>50</RowLimit></View>",
            };
            var items = list.GetItems(q);
            ctx.Load(items, c => c.Include(i => i.Id));
            await ctx.ExecuteQueryAsync();
            return items.Count;
        });

        await ProbeAsync("ENGINE ReconcileFailedBatch (newest-N, no Where)", true, async () =>
        {
            var q = new CamlQuery
            {
                ViewXml = "<View Scope='RecursiveAll'><Query><OrderBy><FieldRef Name='ID' Ascending='FALSE'/>"
                          + "</OrderBy></Query><RowLimit>50</RowLimit></View>",
            };
            var items = list.GetItems(q);
            ctx.Load(items, c => c.Include(i => i.Id));
            await ctx.ExecuteQueryAsync();
            return items.Count;
        });

        await ProbeAsync("ENGINE PruneUpsertMap     (paged RecursiveAll, ID only)", true, async () =>
            await PagedAsync(ctx, list, "<View Scope='RecursiveAll'><ViewFields><FieldRef Name='ID'/></ViewFields><RowLimit Paged='TRUE'>5000</RowLimit></View>", null));

        await ProbeAsync("ENGINE EnsureFolder/GetItemByPath (path-based get)", true, async () =>
        {
            var folder = ctx.Web.GetFolderByServerRelativePath(ResourcePath.FromDecodedUrl(root));
            ctx.Load(folder, f => f.Exists, f => f.ItemCount);
            await ctx.ExecuteQueryAsync();
            return folder.ItemCount;
        });

        // --- shapes that are KNOWN-DANGEROUS (documenting the boundary) ---
        await ProbeAsync("HAZARD Folder.Folders enumeration", false, async () =>
        {
            var folders = ctx.Web.GetFolderByServerRelativePath(ResourcePath.FromDecodedUrl(root)).Folders;
            ctx.Load(folders, fs => fs.Include(f => f.Name));
            await ctx.ExecuteQueryAsync();
            return folders.Count;
        });

        await ProbeAsync("HAZARD Folder.Files enumeration", false, async () =>
        {
            var files = ctx.Web.GetFolderByServerRelativePath(ResourcePath.FromDecodedUrl(root)).Files;
            ctx.Load(files, fs => fs.Include(f => f.Name));
            await ctx.ExecuteQueryAsync();
            return files.Count;
        });

        await ProbeAsync("HAZARD non-paged RecursiveAll (no RowLimit)", false, async () =>
        {
            var items = list.GetItems(new CamlQuery { ViewXml = "<View Scope='RecursiveAll'></View>" });
            ctx.Load(items, c => c.Include(i => i.Id));
            await ctx.ExecuteQueryAsync();
            return items.Count;
        });

        await ProbeAsync("HAZARD Where on FSObjType (non-indexed)", false, async () =>
        {
            var items = list.GetItems(new CamlQuery
            {
                ViewXml = "<View Scope='RecursiveAll'><Query><Where><Eq><FieldRef Name='FSObjType'/>"
                          + "<Value Type='Integer'>1</Value></Eq></Where></Query><RowLimit>100</RowLimit></View>",
            });
            ctx.Load(items, c => c.Include(i => i.Id));
            await ctx.ExecuteQueryAsync();
            return items.Count;
        });

        await ProbeAsync("HAZARD OrderBy Modified (non-indexed)", false, async () =>
        {
            var items = list.GetItems(new CamlQuery
            {
                ViewXml = "<View Scope='RecursiveAll'><Query><OrderBy><FieldRef Name='Modified' Ascending='FALSE'/></OrderBy>"
                          + "</Query><RowLimit>100</RowLimit></View>",
            });
            ctx.Load(items, c => c.Include(i => i.Id));
            await ctx.ExecuteQueryAsync();
            return items.Count;
        });

        await ProbeAsync("BROWSER RenderListDataAsStream (paged, DatesInUtc)", true, async () =>
        {
            var body = new
            {
                parameters = new
                {
                    RenderOptions = 2,
                    DatesInUtc = true,
                    FolderServerRelativeUrl = root,
                    ViewXml = "<View><Query></Query><ViewFields><FieldRef Name='FileLeafRef'/><FieldRef Name='FSObjType'/>"
                              + "<FieldRef Name='Modified'/></ViewFields><RowLimit Paged='TRUE'>200</RowLimit></View>",
                },
            };
            var listUrl = root;
            var resp = await conn.Rest.PostAsync($"{conn.SiteUrl}/_api/web/GetList(@a1)/RenderListDataAsStream?@a1='{listUrl}'", body);
            using var doc = System.Text.Json.JsonDocument.Parse(resp);
            return doc.RootElement.GetProperty("Row").GetArrayLength();
        });

        await ProbeAsync("BROWSER DetectNotebookFolders (/Folders?$expand=Files)", false, async () =>
        {
            var esc = Uri.EscapeDataString(root);
            using var doc = await conn.Rest.GetJsonAsync(
                $"{conn.SiteUrl}/_api/web/GetFolderByServerRelativeUrl('{esc}')/Folders?$expand=Files&$select=ServerRelativeUrl,Files/Name");
            return doc.RootElement.GetProperty("value").GetArrayLength();
        });

        await ProbeAsync("ENGINE PageCopier CreateAllItemsQuery(500)", true, async () =>
        {
            var items = list.GetItems(CamlQuery.CreateAllItemsQuery(500));
            ctx.Load(items, c => c.Include(i => i.Id));
            await ctx.ExecuteQueryAsync();
            return items.Count;
        });

        // ---- THE FOLDER SCAN: paging a >5,000-item SUBFOLDER to exhaustion ----
        // This is what the reported failure hits. A folder-scoped RecursiveAll survives the first pages and
        // then throws deeper in, so anything that stops early (like a 3-page probe) misses it.
        Console.WriteLine();
        Console.WriteLine("  folder scan of a >5,000-item subfolder (paged to EXHAUSTION):");
        var stress = ctx.Web.Lists.GetByTitle(LvtStressTest.SrcLib);
        ctx.Load(stress, l => l.RootFolder.ServerRelativeUrl, l => l.ItemCount);
        await ctx.ExecuteQueryAsync();
        var stressRoot = stress.RootFolder.ServerRelativeUrl;
        var bigFolder = $"{stressRoot}/{LvtStressTest.RootFolder}/Big";      // 6,000 files, one folder
        var wholeTree = $"{stressRoot}/{LvtStressTest.RootFolder}";          // ~11,300 recursive

        await ProbeAsync("OLD    ScanFolder RecursiveAll+folder (Big, all pages)", false, async () =>
            await PagedAsync(ctx, stress, "<View Scope='RecursiveAll'><RowLimit Paged='TRUE'>200</RowLimit></View>", bigFolder, int.MaxValue));

        await ProbeAsync("OLD    ScanFolder RecursiveAll+folder (Stress, all pages)", false, async () =>
            await PagedAsync(ctx, stress, "<View Scope='RecursiveAll'><RowLimit Paged='TRUE'>200</RowLimit></View>", wholeTree, int.MaxValue));

        await ProbeAsync("FIX?   whole-list paged RecursiveAll (no folder scope)", true, async () =>
            await PagedAsync(ctx, stress, "<View Scope='RecursiveAll'><RowLimit Paged='TRUE'>2000</RowLimit></View>", null, int.MaxValue));

        // Default scope does NOT rescue a folder that holds >5,000 DIRECT children (the "Big" folder is flat),
        // which is why the engine's fallback scans the whole list and filters by path rather than using this.
        await ProbeAsync("NOTE   folder scope, DEFAULT scope (flat 6k folder still throws)", false, async () =>
            await PagedAsync(ctx, stress, "<View><RowLimit Paged='TRUE'>2000</RowLimit></View>", bigFolder, int.MaxValue));

        // --- candidate REPLACEMENTS for the two broken calls: which shape gets max-ID safely? ---
        Console.WriteLine();
        Console.WriteLine("  candidate fixes for MaxItemIdAsync:");

        await ProbeAsync("FIX?  RecursiveAll + OrderBy ID desc + RowLimit 1", true, async () =>
        {
            var q = new CamlQuery { ViewXml = "<View Scope='RecursiveAll'><Query><OrderBy><FieldRef Name='ID' Ascending='FALSE'/></OrderBy></Query><RowLimit>1</RowLimit></View>" };
            var top = list.GetItems(q);
            ctx.Load(top, t => t.Include(i => i.Id));
            await ctx.ExecuteQueryAsync();
            return top.AsEnumerable().Select(i => i.Id).DefaultIfEmpty(0).Max();
        });

        await ProbeAsync("FIX?  RecursiveAll + OrderBy ID desc + RowLimit Paged 1", true, async () =>
        {
            var q = new CamlQuery { ViewXml = "<View Scope='RecursiveAll'><Query><OrderBy><FieldRef Name='ID' Ascending='FALSE'/></OrderBy></Query><RowLimit Paged='TRUE'>1</RowLimit></View>" };
            var top = list.GetItems(q);
            ctx.Load(top, t => t.Include(i => i.Id));
            await ctx.ExecuteQueryAsync();
            return top.AsEnumerable().Select(i => i.Id).DefaultIfEmpty(0).Max();
        });

        await ProbeAsync("FIX?  REST /items?$orderby=Id desc&$top=1", true, async () =>
        {
            var esc = Uri.EscapeDataString(root);
            using var doc = await conn.Rest.GetJsonAsync(
                $"{conn.SiteUrl}/_api/web/GetList(@a1)/items?$select=Id&$orderby=Id desc&$top=1&@a1='{esc}'");
            var arr = doc.RootElement.GetProperty("value");
            return arr.GetArrayLength() > 0 ? arr[0].GetProperty("Id").GetInt32() : 0;
        });
    }

    /// <summary>
    /// Pages a CAML query. maxPages caps the walk -- pass int.MaxValue to page to EXHAUSTION, which is the
    /// only way to catch a query that survives its first pages and throws deeper in.
    /// </summary>
    private static async Task<int> PagedAsync(ClientContext ctx, List list, string viewXml, string? folderUrl, int maxPages = 3)
    {
        var query = new CamlQuery { ViewXml = viewXml };
        if (folderUrl != null) query.FolderServerRelativeUrl = folderUrl;
        var total = 0;
        var pages = 0;
        do
        {
            var page = list.GetItems(query);
            ctx.Load(page, p => p.Include(i => i.Id), p => p.ListItemCollectionPosition);
            await ctx.ExecuteQueryAsync();
            total += page.Count;
            query.ListItemCollectionPosition = page.ListItemCollectionPosition;
        } while (query.ListItemCollectionPosition != null && ++pages < maxPages);
        return total;
    }

    private static async Task ProbeAsync(string label, bool expectSafe, Func<Task<int>> run)
    {
        try
        {
            var n = await run();
            Console.WriteLine($"  {(expectSafe ? "  ok  " : " !!!  ")} {label,-52} -> OK ({n} row(s))");
            if (!expectSafe)
                Console.WriteLine($"           (expected this to throw; it did not on this list shape)");
        }
        catch (Exception ex) when (LvtStressTest.IsLvt(ex))
        {
            Console.WriteLine($"  {(expectSafe ? " FAIL " : "  ok  ")} {label,-52} -> THROWS LIST VIEW THRESHOLD");
            Program.Check($"lvt-probe: {label} is threshold-safe", !expectSafe, "threw LVT");
            return;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  {"  ?   "} {label,-52} -> {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
            return;
        }
        if (expectSafe) Program.Check($"lvt-probe: {label} is threshold-safe", true, "no LVT");
    }
}

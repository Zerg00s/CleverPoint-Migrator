using System.Text.Json;
using CleverPoint.Migrator.Core.Auth;
using CleverPoint.Migrator.Core.Csom;
using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Covers the new Fluent-UI behaviours against live tenants:
///   browse-lmas  the large-library HTTP 500 fix (RenderListDataAsStream with no
///                non-indexed OrderBy) on gocleverpointcom/sites/LMAS;
///   batch        copying several lists/libraries in one run, same tenant;
///   batch-cross  the same batch loop across tenants (gocleverpointcom -> cleverpointlab).
/// The batch scenarios replicate the wizard's per-list loop exactly: one
/// CopyEngine.CopyListAsync call per ticked list, results aggregated.
/// </summary>
public static class BatchAndBrowseTests
{
    private const string LmasSite = "https://gocleverpointcom.sharepoint.com/sites/LMAS";

    /// <summary>
    /// Reproduces the user's "HTTP 500 from RenderListDataAsStream" on a 5000+
    /// item library and confirms the fixed (OrderBy-free) query pages cleanly.
    /// </summary>
    public static async Task BrowseLargeLibFixAsync()
    {
        var conn = new SpConnection(LmasSite, new CertTokenProvider(Program.SourceCreds));

        // Find a library above the list view threshold (the screenshot showed
        // "25000 HBTA (Archive)" at 36851, "Documents" at 154317, etc.).
        string? listUrl = null, listTitle = null;
        int itemCount = 0;
        using (var doc = await conn.Rest.GetJsonAsync(
            $"{conn.SiteUrl}/_api/web/lists?$select=Title,Hidden,BaseTemplate,BaseType,ItemCount,RootFolder/ServerRelativeUrl&$expand=RootFolder&$filter=Hidden eq false&$top=500"))
        {
            foreach (var e in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                if (e.GetProperty("BaseType").GetInt32() != 1) continue;   // libraries only
                var count = e.GetProperty("ItemCount").GetInt32();
                if (count <= 5000) continue;
                var url = e.GetProperty("RootFolder").GetProperty("ServerRelativeUrl").GetString();
                if (string.IsNullOrEmpty(url)) continue;
                listUrl = url; listTitle = e.GetProperty("Title").GetString(); itemCount = count;
                break;
            }
        }

        if (!Program.Check("browse-lmas: found a 5000+ item library", listUrl != null,
                listTitle != null ? $"{listTitle} ({itemCount} items)" : "none over threshold on LMAS"))
            return;

        Console.WriteLine($"  testing on '{listTitle}' ({itemCount} items)");

        // The OLD query: <OrderBy> on the non-indexed FileLeafRef. Expected to 500
        // past the threshold. Informational, since some libraries index FileLeafRef.
        var oldOutcome = await TryRenderAsync(conn, listUrl!, withOrderBy: true);
        Console.WriteLine($"  old (OrderBy FileLeafRef): {oldOutcome.Detail}");
        Program.Check("browse-lmas: old OrderBy query reproduces the failure (informational)",
            true, oldOutcome.Ok ? "did not fail on this library" : oldOutcome.Detail);

        // The FIXED query: no OrderBy, page in default (indexed) order.
        var newOutcome = await TryRenderAsync(conn, listUrl!, withOrderBy: false);
        Console.WriteLine($"  new (no OrderBy):          {newOutcome.Detail}");
        Program.Check("browse-lmas: fixed query returns rows without HTTP 500",
            newOutcome.Ok && newOutcome.Rows > 0, newOutcome.Detail);
    }

    /// <summary>
    /// Verifies the per-folder navigation the explorer now uses: the list root
    /// returns its direct children (incl. subfolders), and drilling into one of
    /// those subfolders returns that folder's own children. Mirrors the
    /// production SiteBrowser.GetFolderAsync query (default scope, no OrderBy).
    /// </summary>
    public static async Task FolderNavAsync()
    {
        var conn = new SpConnection(LmasSite, new CertTokenProvider(Program.SourceCreds));

        // Find a library that actually has at least one subfolder at its root.
        string? listUrl = null, listTitle = null, subFolderRef = null, subFolderName = null;
        using (var doc = await conn.Rest.GetJsonAsync(
            $"{conn.SiteUrl}/_api/web/lists?$select=Title,Hidden,BaseTemplate,BaseType,RootFolder/ServerRelativeUrl&$expand=RootFolder&$filter=Hidden eq false&$top=500"))
        {
            foreach (var e in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                if (e.GetProperty("BaseType").GetInt32() != 1) continue;
                var url = e.GetProperty("RootFolder").GetProperty("ServerRelativeUrl").GetString();
                if (string.IsNullOrEmpty(url)) continue;
                var rootRows = await RenderFolderAsync(conn, listServerRelativeUrl: url, folderUrl: url);
                var folder = rootRows.FirstOrDefault(r => r.IsFolder);
                if (folder.Ref is null) continue;
                listUrl = url; listTitle = e.GetProperty("Title").GetString();
                subFolderRef = folder.Ref; subFolderName = folder.Name;
                Program.Check($"browse-nav: list root returns direct children ({listTitle})",
                    rootRows.Count > 0, $"{rootRows.Count} entries at root, {rootRows.Count(x => x.IsFolder)} folders");
                break;
            }
        }

        if (!Program.Check("browse-nav: found a library with a subfolder", subFolderRef != null,
                listTitle != null ? $"{listTitle}" : "no library with root subfolders on LMAS"))
            return;

        // Drill into that subfolder; it should return its own children (no 500).
        var childRows = await RenderFolderAsync(conn, listServerRelativeUrl: listUrl!, folderUrl: subFolderRef!);
        Console.WriteLine($"  opened '{subFolderName}': {childRows.Count} entries inside");
        var allBelow = childRows.All(r => r.Ref.StartsWith(subFolderRef! + "/", StringComparison.OrdinalIgnoreCase));
        Program.Check("browse-nav: drilling into a subfolder returns only its children",
            childRows.Count == 0 || allBelow, $"{childRows.Count} entries, all under the subfolder: {allBelow}");
    }

    private static async Task<List<(string Name, string Ref, bool IsFolder)>> RenderFolderAsync(
        SpConnection conn, string listServerRelativeUrl, string folderUrl)
    {
        var escaped = Uri.EscapeDataString(listServerRelativeUrl.Replace("'", "''"));
        var body = new
        {
            parameters = new
            {
                RenderOptions = 2,
                FolderServerRelativeUrl = folderUrl,
                Paging = (string?)null,
                ViewXml = "<View><Query></Query>"
                    + "<ViewFields><FieldRef Name='FileLeafRef'/><FieldRef Name='FileRef'/><FieldRef Name='FSObjType'/></ViewFields>"
                    + "<RowLimit Paged='TRUE'>500</RowLimit></View>",
            },
        };
        var response = await conn.Rest.PostAsync(
            $"{conn.SiteUrl}/_api/web/GetList(@a1)/RenderListDataAsStream?@a1='{escaped}'", body);
        using var doc = JsonDocument.Parse(response);
        var rows = new List<(string, string, bool)>();
        foreach (var row in doc.RootElement.GetProperty("Row").EnumerateArray())
        {
            var name = row.GetProperty("FileLeafRef").GetString() ?? "";
            if (name == "Forms") continue;
            var isFolder = row.TryGetProperty("FSObjType", out var t) && t.GetString() == "1";
            rows.Add((name, row.GetProperty("FileRef").GetString() ?? "", isFolder));
        }
        return rows;
    }

    private static async Task<(bool Ok, int Rows, string Detail)> TryRenderAsync(SpConnection conn, string listUrl, bool withOrderBy)
    {
        var escaped = Uri.EscapeDataString(listUrl.Replace("'", "''"));
        var orderBy = withOrderBy ? "<OrderBy><FieldRef Name='FileLeafRef'/></OrderBy>" : "";
        var body = new
        {
            parameters = new
            {
                RenderOptions = 2,
                FolderServerRelativeUrl = listUrl,
                Paging = (string?)null,
                ViewXml = $"<View Scope='RecursiveAll'><Query>{orderBy}</Query>"
                    + "<ViewFields><FieldRef Name='FileLeafRef'/><FieldRef Name='FileRef'/><FieldRef Name='FSObjType'/></ViewFields>"
                    + "<RowLimit Paged='TRUE'>500</RowLimit></View>",
            },
        };
        try
        {
            var response = await conn.Rest.PostAsync(
                $"{conn.SiteUrl}/_api/web/GetList(@a1)/RenderListDataAsStream?@a1='{escaped}'", body);
            using var doc = JsonDocument.Parse(response);
            var rows = doc.RootElement.TryGetProperty("Row", out var r) ? r.GetArrayLength() : 0;
            return (true, rows, $"OK, {rows} rows in first page");
        }
        catch (Exception ex)
        {
            var msg = ex.Message.Length > 120 ? ex.Message[..120] + "…" : ex.Message;
            return (false, 0, $"FAILED: {msg}");
        }
    }

    /// <summary>Batch copy within the source tenant: a list and a library in one loop.</summary>
    public static async Task BatchSameTenantAsync()
    {
        var site = await RequireTestSiteAsync();
        var jobs = new[]
        {
            (Source: TestAssets.SourceListTitle, Target: "MigTest-Batch-List", Url: "Lists/MigTestBatchList"),
            (Source: TestAssets.SourceLibTitle,  Target: "MigTest-Batch-Lib",  Url: "MigTestBatchLib"),
        };
        await RunBatchAsync("batch", site, site, jobs, fallback: null);
    }

    /// <summary>Batch copy across tenants: the same list and library to cleverpointlab.</summary>
    public static async Task BatchCrossTenantAsync()
    {
        var site = await RequireTestSiteAsync();

        string? fallback;
        using (var tctx = Program.Target.CreateContext())
        {
            tctx.Load(tctx.Web.SiteUsers, us => us.Include(u => u.LoginName, u => u.Email, u => u.PrincipalType));
            await tctx.ExecuteQueryAsync();
            fallback = tctx.Web.SiteUsers.AsEnumerable()
                .FirstOrDefault(u => u.PrincipalType == Microsoft.SharePoint.Client.Utilities.PrincipalType.User
                    && !string.IsNullOrEmpty(u.Email))?.LoginName;
        }
        Program.Check("batch-cross: fallback user found", fallback != null, fallback);

        var jobs = new[]
        {
            (Source: TestAssets.SourceListTitle, Target: "MigTest-BatchX-List", Url: "Lists/MigTestBatchXList"),
            (Source: TestAssets.SourceLibTitle,  Target: "MigTest-BatchX-Lib",  Url: "MigTestBatchXLib"),
        };
        await RunBatchAsync("batch-cross", site, Program.Target, jobs, fallback);
    }

    private static async Task RunBatchAsync(string tag, SpConnection source, SpConnection target,
        (string Source, string Target, string Url)[] jobs, string? fallback)
    {
        // Clean slate on the target.
        using (var ctx = target.CreateContext())
        {
            foreach (var j in jobs) await TestAssets.DeleteIfExistsAsync(ctx, j.Target);
        }

        int copied = 0, failed = 0, doneLists = 0, failedLists = 0;
        foreach (var j in jobs)
        {
            doneLists++;
            Console.WriteLine($"  [{tag}] {doneLists}/{jobs.Length}: {j.Source} -> {j.Target}");
            var options = new CopyOptions
            {
                TargetListTitle = j.Target,
                TargetListUrl = j.Url,
                UnresolvedUserFallback = fallback,
            };
            try
            {
                var result = await CopyEngine.CopyListAsync(source, target, j.Source, options);
                Console.WriteLine($"    {result.Summary()}");
                foreach (var r in result.Records.Where(r => r.Status is ItemCopyStatus.Failed))
                    Console.WriteLine($"      [FAIL] {r.ItemType} {r.SourcePath}: {r.Message}");
                copied += result.Copied; failed += result.Failed;
                if (result.Failed > 0) failedLists++;
            }
            catch (Exception ex)
            {
                failedLists++;
                Console.WriteLine($"    list crashed: {ex.Message}");
            }
        }

        Program.Check($"{tag}: every list copied without failures", failedLists == 0 && failed == 0,
            $"{doneLists - failedLists}/{jobs.Length} lists clean, {copied} items copied, {failed} item failures");

        // Confirm each target list now exists and holds content.
        using var vctx = target.CreateContext();
        var loaded = jobs.Select(j =>
        {
            var l = vctx.Web.Lists.GetByTitle(j.Target);
            vctx.Load(l, x => x.ItemCount, x => x.Title);
            return (j.Target, List: l);
        }).ToList();
        await vctx.ExecuteQueryAsync();
        foreach (var (title, list) in loaded)
            Program.Check($"{tag}: target '{title}' present with content", list.ItemCount > 0, $"{list.ItemCount} items");
    }

    private static async Task<SpConnection> RequireTestSiteAsync()
    {
        if (Program.TestSite != null) return Program.TestSite;
        var site = await TestAssets.EnsureTestSiteAsync(Program.Source);
        Program.TestSite = site;
        return site;
    }
}

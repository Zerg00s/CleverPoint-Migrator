using System.Diagnostics;
using CleverPoint.Migrator.Core.Auth;
using CleverPoint.Migrator.Core.Csom;
using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Verifies the selected-path fast copy: picking a few files from the LMAS "Documents"
/// library (150K+ items) must NOT scan the whole library. Before this change the copy paged
/// every item (minutes); now the picked paths are fetched directly (seconds).
/// </summary>
public static class FastSelectTest
{
    public static async Task RunAsync()
    {
        var lmas = new SpConnection("https://gocleverpointcom.sharepoint.com/sites/LMAS", new CertTokenProvider(Program.SourceCreds));

        // Grab 3 real file paths from the big library (RowLimit keeps this cheap).
        List<string> paths;
        using (var ctx = lmas.CreateContext())
        {
            var lib = ctx.Web.Lists.GetByTitle("Documents");
            // Threshold-safe: no Where (that would scan >5000 rows on a 150K list); order by
            // the indexed ID and take a small page, then keep files client-side.
            var q = new CamlQuery
            {
                ViewXml = "<View Scope='RecursiveAll'><Query><OrderBy><FieldRef Name='ID' Ascending='TRUE'/></OrderBy></Query><RowLimit>30</RowLimit></View>"
            };
            var page = lib.GetItems(q);
            ctx.Load(page, p => p.Include(i => i["FileRef"], i => i.FileSystemObjectType));
            await ctx.ExecuteQueryAsync();
            paths = page.AsEnumerable().Where(i => i.FileSystemObjectType == FileSystemObjectType.File)
                .Select(i => (string)i["FileRef"]).Take(3).ToList();
        }
        Program.Check("fast-select: got 3 source file paths (setup)", paths.Count == 3,
            string.Join(" | ", paths.Select(p => p.Split('/')[^1])));

        using (var ctx = lmas.CreateContext()) await TestAssets.DeleteIfExistsAsync(ctx, "MigTest-FastSelect");

        var sw = Stopwatch.StartNew();
        var res = await CopyEngine.CopyListAsync(lmas, lmas, "Documents", new CopyOptions
        {
            TargetListTitle = "MigTest-FastSelect",
            TargetListUrl = "MigTestFastSelect",
            SelectedPaths = paths,
        });
        sw.Stop();

        var copied = res.Records.Count(r => r.ItemType == "File" && r.Status == ItemCopyStatus.Copied);
        Console.WriteLine($"  copy of 3 selected files from a 150K-item library took {sw.Elapsed.TotalSeconds:F1}s ({res.Summary()})");
        Program.Check("fast-select: exactly the 3 picked files copied", copied == 3, res.Summary());
        // The old full-library scan took minutes; the fast path should be well under a minute.
        Program.Check("fast-select: no full-library scan (well under a minute)", sw.Elapsed.TotalSeconds < 60, $"{sw.Elapsed.TotalSeconds:F1}s");
    }
}

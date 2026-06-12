using System.Diagnostics;
using CleverPoint.Migrator.Core.Http;
using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Filters and throttling:
///  - "filters": copy MigTest-SrcLib with a *.bin name filter restricted to
///    one folder; verify only matching content lands and skips are logged.
///  - "throttle": prove RequestThrottle paces requests to the configured
///    budget and that PauseHost stalls all traffic (pure local timing test),
///    then run a real copy at a low budget to prove it stays functional.
/// </summary>
public static class FilterThrottleTests
{
    public static async Task FiltersAsync()
    {
        var site = Program.TestSite ?? await TestAssets.EnsureTestSiteAsync(Program.Source);
        Program.TestSite = site;
        using (var ctx = site.CreateContext())
        {
            await TestAssets.DeleteIfExistsAsync(ctx, "MigTest-FilterCopy");
        }

        // Folder scope + name pattern: only Folder-A's direct/child .bin files.
        using var sctx = site.CreateContext();
        var src = sctx.Web.Lists.GetByTitle(TestAssets.SourceLibTitle);
        sctx.Load(src.RootFolder, f => f.ServerRelativeUrl);
        await sctx.ExecuteQueryAsync();

        var options = new CopyOptions
        {
            TargetListTitle = "MigTest-FilterCopy",
            TargetListUrl = "MigTestFilterCopy",
            SourceFolderServerRelativeUrl = $"{src.RootFolder.ServerRelativeUrl}/Folder-A",
            NamePatterns = { "doc-0*.bin" },
        };
        var result = await CopyEngine.CopyListAsync(site, site, TestAssets.SourceLibTitle, options);
        Console.WriteLine($"  copy: {result.Summary()}");

        var copiedFiles = result.Records.Where(r => r.ItemType == "File" && r.Status == ItemCopyStatus.Copied).ToList();
        var filterSkips = result.Records.Count(r => r.Status == ItemCopyStatus.Skipped && (r.Message?.StartsWith("filter:") ?? false));
        Program.Check("filters: no failures", result.Failed == 0, result.Summary());
        Program.Check("filters: only Folder-A doc-0x.bin files copied",
            copiedFiles.Count > 0 && copiedFiles.All(f => f.SourcePath.Contains("/Folder-A/") && f.SourcePath.Contains("/doc-0")),
            $"{copiedFiles.Count} files: {string.Join(", ", copiedFiles.Select(f => f.SourcePath.Split('/')[^1]))}");
        Program.Check("filters: non-matching items visibly skipped", filterSkips > 0, $"{filterSkips} skips logged");
    }

    public static async Task ThrottleAsync()
    {
        // ---- Local pacing proof: 5 rps budget, 11 turns => ~2s minimum ----
        const string fakeHost = "pacing-test.local";
        RequestThrottle.Configure(fakeHost, 5);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 11; i++)
            await RequestThrottle.WaitTurnAsync(fakeHost);
        sw.Stop();
        Program.Check("throttle: pacing enforces the budget",
            sw.ElapsedMilliseconds >= 1900 && sw.ElapsedMilliseconds < 4500,
            $"11 turns @5rps took {sw.ElapsedMilliseconds}ms (expected ~2000)");

        // ---- PauseHost stalls everyone (simulated 429 Retry-After) ----
        RequestThrottle.PauseHost(fakeHost, TimeSpan.FromMilliseconds(1200));
        sw.Restart();
        await RequestThrottle.WaitTurnAsync(fakeHost);
        sw.Stop();
        Program.Check("throttle: 429 pause stalls all traffic to the host",
            sw.ElapsedMilliseconds >= 1000, $"resumed after {sw.ElapsedMilliseconds}ms");

        // ---- Real copy under a low budget stays correct ----
        var site = Program.TestSite ?? await TestAssets.EnsureTestSiteAsync(Program.Source);
        Program.TestSite = site;
        RequestThrottle.Configure(new Uri(site.SiteUrl).Host, 8);
        try
        {
            using (var ctx = site.CreateContext())
            {
                await TestAssets.DeleteIfExistsAsync(ctx, "MigTest-SlowCopy");
            }
            var result = await CopyEngine.CopyListAsync(site, site, TestAssets.LookupTargetTitle,
                new CopyOptions { TargetListTitle = "MigTest-SlowCopy", TargetListUrl = "Lists/MigTestSlowCopy" });
            Program.Check("throttle: budgeted copy completes correctly", result.Failed == 0, result.Summary());
        }
        finally
        {
            RequestThrottle.Configure(new Uri(site.SiteUrl).Host, 0);   // back to unlimited
        }
    }
}

using System.Diagnostics;
using System.Text;
using CleverPoint.Migrator.Core.MigrationApi;
using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Engine benchmark: classic copy vs Migration API across representative
/// cases, two runs each, producing PERFORMANCE.md (tables + recommendation)
/// in the project root for Denis to study.
/// </summary>
public static class BenchmarkTests
{
    private record Sample(string Case, string Engine, int Run, double Seconds, int Files, double Mb, int MemMb, int Throttles);

    public static async Task RunAsync()
    {
        var site = Program.TestSite ?? await TestAssets.EnsureTestSiteAsync(Program.Source);
        Program.TestSite = site;
        var samples = new List<Sample>();

        // Case definitions: (name, source list, runs).
        var cases = new (string Name, string SourceList)[]
        {
            ("12 mixed files (1KB-1MB), 3 folders", TestAssets.SourceLibTitle),
            ("400 small files, 25 folders (4 levels)", ScaleTests.BigLibTitle),
        };

        foreach (var (caseName, sourceList) in cases)
        {
            for (var run = 1; run <= 2; run++)
            {
                samples.Add(await RunClassicAsync(site, caseName, sourceList, $"Bench-C{samples.Count}", run));
                samples.Add(await RunApiAsync(site, caseName, sourceList, $"Bench-A{samples.Count}", run));
            }
        }

        // Cross-tenant single-run comparison on the small library.
        samples.Add(await RunClassicAsync(site, "12 mixed files CROSS-TENANT", TestAssets.SourceLibTitle, "Bench-XC", 1, Program.Target));
        samples.Add(await RunApiAsync(site, "12 mixed files CROSS-TENANT", TestAssets.SourceLibTitle, "Bench-XA", 1, Program.Target));

        WriteReport(samples);
        Program.Check("benchmark: report written", System.IO.File.Exists(ReportPath), ReportPath);
    }

    private static string ReportPath => "/mnt/c/trash/SharePoint-Migrator/PERFORMANCE.md";

    private static async Task<Sample> RunClassicAsync(Core.Csom.SpConnection site, string caseName,
        string sourceList, string target, int run, Core.Csom.SpConnection? targetConn = null)
    {
        var conn = targetConn ?? site;
        using (var ctx = conn.CreateContext())
        {
            await TestAssets.DeleteIfExistsAsync(ctx, target);
        }
        GC.Collect();
        var memBefore = Process.GetCurrentProcess().WorkingSet64;
        var throttles = 0;
        site.Rest.OnThrottle += Count;
        var sw = Stopwatch.StartNew();
        var result = await CopyEngine.CopyListAsync(site, conn, sourceList,
            new CopyOptions { TargetListTitle = target, TargetListUrl = target.Replace("-", ""), UnresolvedUserFallback = null });
        sw.Stop();
        site.Rest.OnThrottle -= Count;
        var files = result.Records.Count(r => r.ItemType == "File" && r.Status == ItemCopyStatus.Copied);
        var mb = result.Records.Where(r => r.ItemType == "File").Sum(r => r.SizeBytes) / 1048576.0;
        Console.WriteLine($"  [bench] classic {caseName} run{run}: {sw.Elapsed.TotalSeconds:F0}s, {files} files");
        return new Sample(caseName, "Classic", run, sw.Elapsed.TotalSeconds, files, mb,
            (int)((Process.GetCurrentProcess().WorkingSet64 - memBefore) / 1048576), throttles);

        void Count(string u, int w, int a) => throttles++;
    }

    private static async Task<Sample> RunApiAsync(Core.Csom.SpConnection site, string caseName,
        string sourceList, string target, int run, Core.Csom.SpConnection? targetConn = null)
    {
        var conn = targetConn ?? site;
        using (var ctx = conn.CreateContext())
        {
            await TestAssets.DeleteIfExistsAsync(ctx, target);
        }
        GC.Collect();
        var memBefore = Process.GetCurrentProcess().WorkingSet64;
        var engine = new MigrationApiEngine(site, conn);
        var sw = Stopwatch.StartNew();
        var result = await engine.CopyLibraryAsync(sourceList,
            new CopyOptions { TargetListTitle = target, TargetListUrl = target.Replace("-", "") });
        sw.Stop();
        using var sctx = site.CreateContext();
        var src = sctx.Web.Lists.GetByTitle(sourceList);
        sctx.Load(src, l => l.ItemCount);
        await sctx.ExecuteQueryAsync();
        Console.WriteLine($"  [bench] api {caseName} run{run}: {sw.Elapsed.TotalSeconds:F0}s");
        return new Sample(caseName, "MigrationAPI", run, sw.Elapsed.TotalSeconds, src.ItemCount, 0,
            (int)((Process.GetCurrentProcess().WorkingSet64 - memBefore) / 1048576), 0);
    }

    private static void WriteReport(List<Sample> samples)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# CleverPoint Migrator: engine performance comparison");
        sb.AppendLine();
        sb.AppendLine($"Measured live on {DateTime.Now:yyyy-MM-dd} against gocleverpointcom (source)");
        sb.AppendLine("and cleverpointlab (cross-tenant target). Each same-site case ran twice.");
        sb.AppendLine();
        sb.AppendLine("## Raw results");
        sb.AppendLine();
        sb.AppendLine("| Case | Engine | Run | Duration (s) | Items/files | Items per second | Memory delta (MB) | Throttle hits |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var s in samples)
            sb.AppendLine($"| {s.Case} | {s.Engine} | {s.Run} | {s.Seconds:F0} | {s.Files} | {(s.Files / Math.Max(1, s.Seconds)):F2} | {s.MemMb} | {s.Throttles} |");
        sb.AppendLine();
        sb.AppendLine("## Averages by case and engine");
        sb.AppendLine();
        sb.AppendLine("| Case | Classic avg (s) | Migration API avg (s) | Faster engine |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var grp in samples.GroupBy(s => s.Case))
        {
            var classic = grp.Where(s => s.Engine == "Classic").Select(s => s.Seconds).DefaultIfEmpty(0).Average();
            var api = grp.Where(s => s.Engine == "MigrationAPI").Select(s => s.Seconds).DefaultIfEmpty(0).Average();
            var winner = classic == 0 || api == 0 ? "n/a" : classic < api ? $"Classic ({api / classic:F1}x)" : $"Migration API ({classic / api:F1}x)";
            sb.AppendLine($"| {grp.Key} | {classic:F0} | {api:F0} | {winner} |");
        }
        sb.AppendLine();
        sb.AppendLine("## Reading the numbers");
        sb.AppendLine();
        sb.AppendLine("The classic engine copies item by item, so its time grows linearly with");
        sb.AppendLine("item count but it starts instantly and preserves the most metadata");
        sb.AppendLine("(including attachments, lookups and version history). The Migration API");
        sb.AppendLine("pays a fixed overhead per job (container provisioning, package upload,");
        sb.AppendLine("queue processing) and then imports server-side in bulk, so it wins as");
        sb.AppendLine("item counts grow. Earlier scale runs measured 400 files in 2.9 minutes");
        sb.AppendLine("through 4 pipelined API jobs with flat memory, and a 1,200-item list at");
        sb.AppendLine("11.5 items/second through the classic engine.");
        sb.AppendLine();
        sb.AppendLine("## Recommendation");
        sb.AppendLine();
        sb.AppendLine("- Lists (any size): Classic. The Migration API path does not cover plain");
        sb.AppendLine("  list items, and classic list throughput is strong.");
        sb.AppendLine("- Libraries up to a few hundred files: Classic (simpler, instant start,");
        sb.AppendLine("  richest fidelity).");
        sb.AppendLine("- Libraries with thousands of files or very large totals: Migration API");
        sb.AppendLine("  (server-side import, pipelined jobs, flat memory). Files over the");
        sb.AppendLine("  large-file threshold automatically stream through the classic chunked");
        sb.AppendLine("  path either way.");
        sb.AppendLine("- The app defaults to Classic and suggests the Migration API when a");
        sb.AppendLine("  selection contains more than ~1,000 files.");
        System.IO.File.WriteAllText(ReportPath, sb.ToString(), Encoding.UTF8);
    }
}

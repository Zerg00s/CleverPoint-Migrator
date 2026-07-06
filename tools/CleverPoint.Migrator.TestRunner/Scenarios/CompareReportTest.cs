using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using CleverPoint.Migrator.Core.Validation;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Client-ready verification report (F4). Self-contained: provisions the source library,
/// copies it, then compares with SHA-256 sampling and asserts the report carries the measured
/// evidence a hand-off deliverable needs (matched count, hash coverage, storage, sampling
/// disclosure) and that the HTML actually renders those.
/// </summary>
public static class CompareReportTest
{
    public static async Task RunAsync()
    {
        var site = Program.TestSite ?? await TestAssets.EnsureTestSiteAsync(Program.Source);
        Program.TestSite = site;

        // Fresh source + copy so the compare has a known, clean pair to certify.
        await TestAssets.RecreateSourceLibraryAsync(site);
        using (var ctx = site.CreateContext()) await TestAssets.DeleteIfExistsAsync(ctx, "MigTest-CopyLib");
        var copy = await CopyEngine.CopyListAsync(site, site, TestAssets.SourceLibTitle, new CopyOptions
        {
            TargetListTitle = "MigTest-CopyLib",
            TargetListUrl = "MigTestCopyLib",
        });
        Program.Check("compare: source library copied", copy.Failed == 0 && copy.Copied > 0, copy.Summary());

        var report = await CompareReport.RunAsync(site, site,
            TestAssets.SourceLibTitle, "MigTest-CopyLib",
            new[] { "DocCategory" }, compareContent: true, contentSampleEvery: 4);
        report.ClientName = "Contoso Ltd";
        report.PreparedBy = "Denis Molodtsov";
        report.ToolVersion = "1.0.5";

        Program.Check("compare: report clean", report.IsClean, $"{report.Verdict}: {report.Mismatches.Count} differences");
        Program.Check("compare: counts captured", report.SourceItems > 0 && report.TargetItems == report.SourceItems,
            $"{report.SourceItems} vs {report.TargetItems}");
        Program.Check("compare: items paired == source items", report.ItemsPaired == report.SourceItems,
            $"{report.ItemsPaired}/{report.SourceItems}");

        // Evidence: the library has 12 files; 1-in-4 sampling hashes a real subset, over real bytes.
        Program.Check("compare: SHA-256 files hashed (1-in-4 of the files)",
            report.FilesHashVerified > 0 && report.FilesHashVerified < report.SourceFiles,
            $"{report.FilesHashVerified} of {report.SourceFiles} files hashed");
        Program.Check("compare: bytes were hashed", report.BytesHashVerified > 0, $"{report.BytesHashVerified} bytes");
        Program.Check("compare: target storage measured", report.StorageBytes > 0, $"{report.StorageBytes} bytes");

        // Sampling MUST be disclosed in the verdict so a sampled pass isn't read as full.
        Program.Check("compare: verdict discloses sampling", report.Verdict.Contains("1-in-4"), report.Verdict);

        var htmlPath = Path.Combine(Directory.GetCurrentDirectory(), "verification-report.html");
        var csvPath = Path.Combine(Directory.GetCurrentDirectory(), "verification-report.csv");
        report.ExportHtml(htmlPath);
        report.ExportCsv(csvPath);
        var html = await System.IO.File.ReadAllTextAsync(htmlPath);
        Program.Check("compare: HTML shows VERIFIED badge", html.Contains(">VERIFIED<"), "badge");
        Program.Check("compare: HTML shows SHA-256 coverage + sampling", html.Contains("SHA-256 coverage") && html.Contains("1-in-4"), "coverage");
        Program.Check("compare: HTML carries branding (client + version)",
            html.Contains("Contoso Ltd") && html.Contains("v1.0.5"), "branding");
        Program.Check("compare: HTML + CSV exported",
            System.IO.File.Exists(csvPath) && new FileInfo(htmlPath).Length > 800,
            $"{new FileInfo(htmlPath).Length} bytes html");
        Console.WriteLine($"  report written: {htmlPath}");
    }
}

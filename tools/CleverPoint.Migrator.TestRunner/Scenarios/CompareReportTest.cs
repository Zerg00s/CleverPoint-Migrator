using CleverPoint.Migrator.Core.Validation;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>Compare report feature smoke test against an already-verified pair.</summary>
public static class CompareReportTest
{
    public static async Task RunAsync()
    {
        var site = Program.TestSite ?? await TestAssets.EnsureTestSiteAsync(Program.Source);
        Program.TestSite = site;

        var report = await CompareReport.RunAsync(site, site,
            TestAssets.SourceLibTitle, "MigTest-CopyLib",
            new[] { "DocCategory" }, compareContent: true, contentSampleEvery: 4);

        Program.Check("compare: report clean", report.IsClean, $"{report.Verdict}: {report.Mismatches.Count} differences");
        Program.Check("compare: counts captured", report.SourceItems > 0 && report.TargetItems == report.SourceItems,
            $"{report.SourceItems} vs {report.TargetItems}");

        var htmlPath = Path.Combine(Directory.GetCurrentDirectory(), "compare-report.html");
        var csvPath = Path.Combine(Directory.GetCurrentDirectory(), "compare-report.csv");
        report.ExportHtml(htmlPath);
        report.ExportCsv(csvPath);
        Program.Check("compare: HTML + CSV exported",
            System.IO.File.Exists(htmlPath) && System.IO.File.Exists(csvPath),
            $"{new FileInfo(htmlPath).Length} bytes html");
    }
}

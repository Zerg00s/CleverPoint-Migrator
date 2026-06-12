using CleverPoint.Migrator.Core.MigrationApi;
using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Validation;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// End-to-end Migration API (Azure blob) test: package MigTest-SrcLib,
/// upload to SharePoint-provided encrypted containers, run the import job
/// into MigTest-ApiLib on the same test site, then verify with the same
/// verifier as the classic engine (SHA-256 content + metadata).
/// </summary>
public static class MigrationApiTest
{
    public static async Task RunAsync()
    {
        var site = Program.TestSite ?? await TestAssets.EnsureTestSiteAsync(Program.Source);
        Program.TestSite = site;

        using (var ctx = site.CreateContext())
        {
            await TestAssets.DeleteIfExistsAsync(ctx, "MigTest-ApiLib");
        }

        var engine = new MigrationApiEngine(site, site);
        engine.OnProgress += msg => Console.WriteLine($"  [api] {msg}");

        var options = new CopyOptions
        {
            TargetListTitle = "MigTest-ApiLib",
            TargetListUrl = "MigTestApiLib",
        };
        var result = await engine.CopyLibraryAsync(TestAssets.SourceLibTitle, options);
        Console.WriteLine($"  result: {result.Summary()}");
        foreach (var r in result.Records.Where(r => r.Status is ItemCopyStatus.Failed or ItemCopyStatus.Warning).Take(10))
            Console.WriteLine($"    [{r.Status}] {r.ItemType}: {r.Message}");

        Program.Check("migration api: job completed without failures", result.Failed == 0, result.Summary());

        // Verify the imported library against the source.
        using var sourceCtx = site.CreateContext();
        using var targetCtx = site.CreateContext();
        var sourceList = sourceCtx.Web.Lists.GetByTitle(TestAssets.SourceLibTitle);
        var targetList = targetCtx.Web.Lists.GetByTitle("MigTest-ApiLib");
        var verifier = new CopyVerifier(sourceCtx, targetCtx);
        var mismatches = await verifier.VerifyAsync(sourceList, targetList,
            new[] { "DocCategory" }, compareFileContent: true);
        foreach (var m in mismatches.Take(20)) Console.WriteLine($"    MISMATCH: {m}");
        Program.Check("migration api: verification clean (incl. SHA-256)", mismatches.Count == 0, $"{mismatches.Count} mismatches");
    }
}

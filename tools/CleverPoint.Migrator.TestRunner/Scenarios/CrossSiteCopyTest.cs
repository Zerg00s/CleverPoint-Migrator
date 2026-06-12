using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using CleverPoint.Migrator.Core.Validation;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Cross-site, same-tenant copy: migtest subsite -> DemoLargeSite parent web.
/// Users exist on both webs, so full user verification applies.
/// </summary>
public static class CrossSiteCopyTest
{
    public static async Task RunAsync()
    {
        var site = Program.TestSite ?? await TestAssets.EnsureTestSiteAsync(Program.Source);
        Program.TestSite = site;

        using (var ctx = Program.Source.CreateContext())
        {
            await TestAssets.DeleteIfExistsAsync(ctx, "MigTest-CrossSite");
        }

        // Copy the lookup TARGET list first so the main list's lookup field
        // can be rewired (cross-web lookups match by list title) and values
        // can be translated by display value.
        var lookupResult = await CopyEngine.CopyListAsync(site, Program.Source, TestAssets.LookupTargetTitle,
            new CopyOptions { TargetListTitle = TestAssets.LookupTargetTitle, TargetListUrl = "Lists/MigTestLookupTarget" });
        Program.Check("cross-site copy: lookup target list copied", lookupResult.Failed == 0, lookupResult.Summary());

        var options = new CopyOptions
        {
            TargetListTitle = "MigTest-CrossSite",
            TargetListUrl = "Lists/MigTestCrossSite",
        };
        var result = await CopyEngine.CopyListAsync(site, Program.Source, TestAssets.SourceListTitle, options);
        Console.WriteLine($"  copy result: {result.Summary()}");
        foreach (var r in result.Records.Where(r => r.Status is ItemCopyStatus.Failed or ItemCopyStatus.Warning).Take(8))
            Console.WriteLine($"    [{r.Status}] {r.ItemType} {r.SourcePath}: {r.Message}");

        Program.Check("cross-site copy: no failures", result.Failed == 0, result.Summary());

        using var sourceCtx = site.CreateContext();
        using var targetCtx = Program.Source.CreateContext();
        var sourceList = sourceCtx.Web.Lists.GetByTitle(TestAssets.SourceListTitle);
        var targetList = targetCtx.Web.Lists.GetByTitle("MigTest-CrossSite");
        var verifier = new CopyVerifier(sourceCtx, targetCtx);
        var mismatches = await verifier.VerifyAsync(sourceList, targetList,
            new[] { "Title", "TextCol", "NumberCol", "DateCol", "ChoiceCol", "NotesCol", "FlagCol", "MoneyCol", "LinkCol", "PersonCol", "LookupCol" });
        foreach (var m in mismatches.Take(15)) Console.WriteLine($"    MISMATCH: {m}");
        Program.Check("cross-site copy: verification clean (incl. users)", mismatches.Count == 0, $"{mismatches.Count} mismatches");
    }
}

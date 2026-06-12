using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using CleverPoint.Migrator.Core.Validation;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Self-healing: copy a library, then deliberately corrupt a target file
/// (overwrite with 1 byte) and let the healing pass detect the size
/// mismatch, delete the corrupt copy, and re-migrate it. Final state must
/// hash-verify clean.
/// </summary>
public static class HealingTests
{
    public static async Task RunAsync()
    {
        var site = Program.TestSite ?? await TestAssets.EnsureTestSiteAsync(Program.Source);
        Program.TestSite = site;
        using (var ctx = site.CreateContext())
        {
            await TestAssets.DeleteIfExistsAsync(ctx, "MigTest-HealCopy");
        }

        var options = new CopyOptions { TargetListTitle = "MigTest-HealCopy", TargetListUrl = "MigTestHealCopy" };
        var first = await CopyEngine.CopyListAsync(site, site, TestAssets.SourceLibTitle, options);
        Program.Check("healing: initial copy clean", first.Failed == 0, first.Summary());

        // ---- Sabotage: truncate doc-05.bin on the TARGET to 1 byte ----
        using (var ctx = site.CreateContext())
        {
            var lib = ctx.Web.Lists.GetByTitle("MigTest-HealCopy");
            ctx.Load(lib.RootFolder, f => f.ServerRelativeUrl);
            await ctx.ExecuteQueryAsync();
            ctx.Web.GetFolderByServerRelativeUrl(lib.RootFolder.ServerRelativeUrl).Files.Add(new FileCreationInformation
            {
                Url = "doc-05.bin",
                ContentStream = new MemoryStream(new byte[] { 1 }),
                Overwrite = true,
            });
            await ctx.ExecuteQueryAsync();
            Console.WriteLine("  sabotaged: target doc-05.bin truncated to 1 byte");
        }

        // ---- Healing pass on the EXISTING migration (no fresh full copy):
        //      it must find the truncated file and fix exactly that. ----
        var healing = new HealingOptions { AutoRetry = true, MaxRetries = 5, RepairCorruptFiles = true };
        var healed = await RunCoordinator.HealAsync(site, site, TestAssets.SourceLibTitle, options, healing, null,
            msg => Console.WriteLine($"  [heal] {msg}"));

        var repaired = healed.Records.Count(r => r.ItemType == "File" && r.Status == ItemCopyStatus.Copied
            && (r.Message?.StartsWith("healing #") ?? false) && r.SourcePath.EndsWith("doc-05.bin"));
        Program.Check("healing: corrupt file detected and re-migrated", repaired >= 1, $"{repaired} repair record(s)");

        using var sctx = site.CreateContext();
        using var tctx = site.CreateContext();
        var verifier = new CopyVerifier(sctx, tctx);
        var mismatches = await verifier.VerifyAsync(
            sctx.Web.Lists.GetByTitle(TestAssets.SourceLibTitle),
            tctx.Web.Lists.GetByTitle("MigTest-HealCopy"),
            new[] { "DocCategory" }, compareFileContent: true);
        foreach (var m in mismatches.Take(8)) Console.WriteLine($"    MISMATCH: {m}");
        Program.Check("healing: final state hash-verifies clean", mismatches.Count == 0, $"{mismatches.Count} mismatches");
    }
}

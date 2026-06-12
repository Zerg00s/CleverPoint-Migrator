using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using CleverPoint.Migrator.Core.Validation;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Forces the ValidateUpdateListItem document-update metadata strategy (the
/// path browser-auth runs switch to) and verifies dates AND authors land.
/// </summary>
public static class DocMetaFallbackTest
{
    public static async Task RunAsync()
    {
        var site = Program.TestSite ?? Program.Source.ForWeb($"{Program.Source.SiteUrl}/migtest");
        using (var ctx = site.CreateContext())
        {
            await TestAssets.DeleteIfExistsAsync(ctx, "MigTest-MetaFallback");
        }

        using var sourceCtx = site.CreateContext();
        using var targetCtx = site.CreateContext();
        var users = new UserResolver(sourceCtx, targetCtx);
        var sourceList = sourceCtx.Web.Lists.GetByTitle(TestAssets.SourceLibTitle);

        var schema = new SchemaCopier(sourceCtx, targetCtx);
        var options = new CopyOptions { TargetListTitle = "MigTest-MetaFallback", TargetListUrl = "MigTestMetaFallback" };
        var result = new CopyResult();
        var targetList = await schema.CopyAsync(sourceList, options, result);

        var copier = new FileCopier(sourceCtx, targetCtx, users, site.Rest, site.Rest)
        {
            ForceFormUpdateMetadata = true,
        };
        await copier.CopyAsync(sourceList, targetList, options, result);
        Console.WriteLine($"  copy result: {result.Summary()}");
        foreach (var r in result.Records.Where(r => r.Status is ItemCopyStatus.Failed or ItemCopyStatus.Warning))
            Console.WriteLine($"    [{r.Status}] {r.ItemType} {r.SourcePath}: {r.Message}");
        Program.Check("meta-fallback: no failures", result.Failed == 0, result.Summary());

        // Field-by-field verification INCLUDING Created/Modified/Author/Editor.
        var verifier = new CopyVerifier(sourceCtx, targetCtx);
        var mismatches = await verifier.VerifyAsync(
            sourceCtx.Web.Lists.GetByTitle(TestAssets.SourceLibTitle),
            targetCtx.Web.Lists.GetByTitle("MigTest-MetaFallback"),
            new[] { "DocCategory" });
        foreach (var m in mismatches.Take(20)) Console.WriteLine($"    MISMATCH: {m}");
        Program.Check("meta-fallback: dates and authors preserved via form-update strategy",
            mismatches.Count == 0, $"{mismatches.Count} mismatches");
    }
}

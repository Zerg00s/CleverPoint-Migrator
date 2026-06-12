using CleverPoint.Migrator.Core.MigrationApi;
using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Validation;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Migration API engine CROSS-TENANT: source library on gocleverpointcom,
/// import jobs running against cleverpointlab. Provisioned containers,
/// queue and encryption all live on the target tenant; users fall back.
/// </summary>
public static class CrossTenantApiTest
{
    public static async Task RunAsync()
    {
        var site = Program.TestSite ?? await TestAssets.EnsureTestSiteAsync(Program.Source);
        Program.TestSite = site;

        string? fallback;
        using (var tctx = Program.Target.CreateContext())
        {
            tctx.Load(tctx.Web.SiteUsers, us => us.Include(u => u.LoginName, u => u.Email, u => u.PrincipalType));
            await tctx.ExecuteQueryAsync();
            fallback = tctx.Web.SiteUsers.AsEnumerable()
                .FirstOrDefault(u => u.PrincipalType == Microsoft.SharePoint.Client.Utilities.PrincipalType.User
                    && !string.IsNullOrEmpty(u.Email))?.LoginName;
        }

        using (var dctx = Program.Target.CreateContext())
        {
            await TestAssets.DeleteIfExistsAsync(dctx, "MigTest-XtApiLib");
        }

        var engine = new MigrationApiEngine(site, Program.Target);
        engine.OnProgress += msg => Console.WriteLine($"  [api] {msg}");
        var result = await engine.CopyLibraryAsync(TestAssets.SourceLibTitle, new CopyOptions
        {
            TargetListTitle = "MigTest-XtApiLib",
            TargetListUrl = "MigTestXtApiLib",
            UnresolvedUserFallback = fallback,
        });
        Console.WriteLine($"  result: {result.Summary()}");
        foreach (var r in result.Records.Where(r => r.Status == ItemCopyStatus.Failed).Take(8))
            Console.WriteLine($"    [Failed] {r.ItemType}: {r.Message}");
        Program.Check("xt-api: cross-tenant job no failures", result.Failed == 0, result.Summary());

        using var sourceCtx = site.CreateContext();
        using var targetCtx = Program.Target.CreateContext();
        var verifier = new CopyVerifier(sourceCtx, targetCtx);
        var mismatches = await verifier.VerifyAsync(
            sourceCtx.Web.Lists.GetByTitle(TestAssets.SourceLibTitle),
            targetCtx.Web.Lists.GetByTitle("MigTest-XtApiLib"),
            new[] { "DocCategory" }, compareFileContent: true, compareUsers: false);
        foreach (var m in mismatches.Take(12)) Console.WriteLine($"    MISMATCH: {m}");
        Program.Check("xt-api: verification clean (fields, dates, SHA-256)", mismatches.Count == 0, $"{mismatches.Count} mismatches");
    }
}

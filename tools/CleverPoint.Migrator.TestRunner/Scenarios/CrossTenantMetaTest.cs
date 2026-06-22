using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using CleverPoint.Migrator.Core.Validation;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Cross-tenant copy of a list with the FULL spread of column types — text,
/// number, date, choice, multiline, boolean, currency, hyperlink, person and a
/// LOOKUP — verifying every field (incl. the lookup, matched by display value)
/// resolves on the target tenant. The lookup's referenced list is provisioned on
/// the target first so the lookup can bind.
/// </summary>
public static class CrossTenantMetaTest
{
    public static async Task RunAsync()
    {
        var site = Program.TestSite ?? await TestAssets.EnsureTestSiteAsync(Program.Source);
        Program.TestSite = site;

        // Fallback account on the target tenant for unresolved source users.
        string? fallback;
        using (var tctx = Program.Target.CreateContext())
        {
            tctx.Load(tctx.Web.SiteUsers, us => us.Include(u => u.LoginName, u => u.Email, u => u.PrincipalType));
            await tctx.ExecuteQueryAsync();
            fallback = tctx.Web.SiteUsers.AsEnumerable()
                .FirstOrDefault(u => u.PrincipalType == Microsoft.SharePoint.Client.Utilities.PrincipalType.User
                    && !string.IsNullOrEmpty(u.Email))?.LoginName;
        }
        Program.Check("cross-meta: fallback user found", fallback != null, fallback);

        // The lookup column points at a list that must exist on the TARGET so the
        // engine can remap lookup values across tenants (it matches by display value).
        using (var tctx = Program.Target.CreateContext())
        {
            await TestAssets.EnsureLookupTargetAsync(tctx);
            await TestAssets.DeleteIfExistsAsync(tctx, "MigTest-CrossMeta");
        }

        var options = new CopyOptions
        {
            TargetListTitle = "MigTest-CrossMeta",
            TargetListUrl = "Lists/MigTestCrossMeta",
            UnresolvedUserFallback = fallback,
        };
        var result = await CopyEngine.CopyListAsync(site, Program.Target, TestAssets.SourceListTitle, options);
        Console.WriteLine($"  copy result: {result.Summary()}");
        foreach (var r in result.Records.Where(r => r.Status is ItemCopyStatus.Failed or ItemCopyStatus.Warning))
            Console.WriteLine($"    [{r.Status}] {r.ItemType} {r.SourcePath}: {r.Message}");

        Program.Check("cross-meta: no failures", result.Failed == 0, result.Summary());
        Program.Check("cross-meta: items copied",
            result.Records.Count(r => r.ItemType == "Item" && r.Status == ItemCopyStatus.Copied) == 25,
            $"{result.Records.Count(r => r.ItemType == "Item" && r.Status == ItemCopyStatus.Copied)} items");

        using var sourceCtx = site.CreateContext();
        using var targetCtx = Program.Target.CreateContext();
        var sourceList = sourceCtx.Web.Lists.GetByTitle(TestAssets.SourceListTitle);
        var targetList = targetCtx.Web.Lists.GetByTitle("MigTest-CrossMeta");
        var verifier = new CopyVerifier(sourceCtx, targetCtx);

        // Every type EXCEPT person (identities don't cross tenants) — including LookupCol.
        var fields = new[] { "Title", "TextCol", "NumberCol", "DateCol", "ChoiceCol", "NotesCol", "FlagCol", "MoneyCol", "LinkCol", "LookupCol" };
        var mismatches = await verifier.VerifyAsync(sourceList, targetList, fields, compareUsers: false);
        foreach (var m in mismatches.Take(20)) Console.WriteLine($"    MISMATCH: {m}");
        Program.Check("cross-meta: all column types verify clean (incl. lookup)", mismatches.Count == 0, $"{mismatches.Count} mismatches");
    }
}

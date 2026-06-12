using CleverPoint.Migrator.Core.Csom;
using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using CleverPoint.Migrator.Core.Validation;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Cross-tenant copy: MigTest-Source (gocleverpointcom/migtest) to the
/// cleverpointlab root site. Source users do not exist on the target, so the
/// resolver must fall back to a configured account; dates and field values
/// must still copy exactly.
/// </summary>
public static class CrossTenantCopyTests
{
    public static async Task RunAsync()
    {
        var site = Program.TestSite ?? await TestAssets.EnsureTestSiteAsync(Program.Source);
        Program.TestSite = site;

        // Pick a fallback account that actually exists on the target tenant.
        string? fallback = null;
        using (var tctx = Program.Target.CreateContext())
        {
            tctx.Load(tctx.Web.SiteUsers, us => us.Include(u => u.LoginName, u => u.Email, u => u.PrincipalType));
            await tctx.ExecuteQueryAsync();
            fallback = tctx.Web.SiteUsers.AsEnumerable()
                .FirstOrDefault(u => u.PrincipalType == Microsoft.SharePoint.Client.Utilities.PrincipalType.User
                    && !string.IsNullOrEmpty(u.Email))?.LoginName;
        }
        Program.Check("cross-tenant: fallback user found", fallback != null, fallback);

        using (var ctx = Program.Target.CreateContext())
        {
            await TestAssets.DeleteIfExistsAsync(ctx, "MigTest-CrossCopy");
        }

        var options = new CopyOptions
        {
            TargetListTitle = "MigTest-CrossCopy",
            TargetListUrl = "Lists/MigTestCrossCopy",
            UnresolvedUserFallback = fallback,
        };
        var result = await CopyEngine.CopyListAsync(site, Program.Target, TestAssets.SourceListTitle, options);
        Console.WriteLine($"  copy result: {result.Summary()}");
        var unresolvedWarnings = result.Records.Count(r => r.ItemType == "User" && r.Status == ItemCopyStatus.Warning);
        Console.WriteLine($"  unresolved users mapped to fallback: {unresolvedWarnings}");

        Program.Check("cross-tenant copy: no failures", result.Failed == 0, result.Summary());
        Program.Check("cross-tenant copy: items copied",
            result.Records.Count(r => r.ItemType == "Item" && r.Status == ItemCopyStatus.Copied) == 25);

        // Users cannot match across tenants (different identities), so verify
        // everything else: fields (minus person), dates, structure.
        using var sourceCtx = site.CreateContext();
        using var targetCtx = Program.Target.CreateContext();
        var sourceList = sourceCtx.Web.Lists.GetByTitle(TestAssets.SourceListTitle);
        var targetList = targetCtx.Web.Lists.GetByTitle("MigTest-CrossCopy");
        var verifier = new CopyVerifier(sourceCtx, targetCtx);
        var mismatches = await verifier.VerifyAsync(sourceList, targetList,
            new[] { "Title", "TextCol", "NumberCol", "DateCol", "ChoiceCol", "NotesCol", "FlagCol", "MoneyCol", "LinkCol" },
            compareUsers: false);
        foreach (var m in mismatches.Take(15)) Console.WriteLine($"    MISMATCH: {m}");
        Program.Check("cross-tenant copy: verification clean (fields+dates)", mismatches.Count == 0, $"{mismatches.Count} mismatches");
    }
}

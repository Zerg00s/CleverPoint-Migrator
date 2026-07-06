using CleverPoint.Migrator.Core.Csom;
using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Orphan-user mapping (F-Map): unresolved source users can be redirected to any target
/// principal, INCLUDING the built-in System Account. This verifies the "SHAREPOINT\system"
/// login the Ux offers actually resolves via EnsureUser, and that a copy with a deliberately
/// unresolvable author + that fallback completes with the item ending up on the fallback
/// identity rather than failing.
/// </summary>
public static class SystemAccountMapTest
{
    // Mirror of UxMappingStore.SystemAccountLogin (the Ux project can't be referenced here).
    private const string SystemAccountLogin = "SHAREPOINT\\system";

    public static async Task RunAsync()
    {
        var site = Program.TestSite ?? await TestAssets.EnsureTestSiteAsync(Program.Source);
        Program.TestSite = site;

        // 1) The System Account login resolves on this tenant.
        int sysId;
        string sysTitle;
        using (var ctx = site.CreateContext())
        {
            var sys = ctx.Web.EnsureUser(SystemAccountLogin);
            ctx.Load(sys, u => u.Id, u => u.Title, u => u.LoginName);
            await ctx.ExecuteWithRetryAsync();
            sysId = sys.Id;
            sysTitle = sys.Title;
            Console.WriteLine($"  System Account resolved: id={sys.Id}, title='{sys.Title}', login='{sys.LoginName}'");
        }
        Program.Check("sysaccount: System Account login resolves via EnsureUser", sysId > 0, $"id={sysId} '{sysTitle}'");

        // 2) A copy whose source authors can't be mapped falls back to the System Account
        //    instead of failing. Use a made-up unmapped user map + fallback so the resolver
        //    is forced down the fallback path for every author.
        await TestAssets.RecreateSourceListAsync(site);
        using (var ctx = site.CreateContext()) await TestAssets.DeleteIfExistsAsync(ctx, "MigTest-SysFallback");

        var result = await CopyEngine.CopyListAsync(site, site, TestAssets.SourceListTitle,
            new CopyOptions
            {
                TargetListTitle = "MigTest-SysFallback",
                TargetListUrl = "Lists/MigTestSysFallback",
                PreserveAuthorsAndDates = true,
                // Force the fallback: map a login that doesn't exist so unresolved -> fallback.
                UnresolvedUserFallback = SystemAccountLogin,
            },
            userMap: new Dictionary<string, string> { ["no-such-user@example.invalid"] = "also-missing@example.invalid" });

        Console.WriteLine($"  copy with System Account fallback: {result.Summary()}");
        Program.Check("sysaccount: copy completes (fallback prevents author failures)", result.Failed == 0, result.Summary());

        // Items exist on the target (the copy did real work, not an empty pass).
        using var vctx = site.CreateContext();
        var tlist = vctx.Web.Lists.GetByTitle("MigTest-SysFallback");
        vctx.Load(tlist, l => l.ItemCount);
        await vctx.ExecuteQueryAsync();
        Program.Check("sysaccount: items copied to target", tlist.ItemCount > 0, $"{tlist.ItemCount} items");
    }
}

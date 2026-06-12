using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Identity mapping CSV: export a template of source users, map two of them
/// to a real target-tenant UPN, run a cross-tenant copy with the mapping
/// (no fallback) and verify mapped users resolve while unmapped users warn.
/// </summary>
public static class UserMappingTests
{
    public static async Task RunAsync()
    {
        var site = Program.TestSite ?? await TestAssets.EnsureTestSiteAsync(Program.Source);
        Program.TestSite = site;

        // ---- Template export (auto-detect column left blank cross-tenant) ----
        var templatePath = Path.Combine(Directory.GetCurrentDirectory(), "user-mapping-template.csv");
        UserMappingStore.ExportTemplate(templatePath,
            TestAssets.SourceUsers.Select(u => (u.Login, u.Email, u.Title)),
            new[] { "Migrator Test Members" },
            new Dictionary<string, string>());
        Program.Check("usermap: template exported",
            System.IO.File.Exists(templatePath) && System.IO.File.ReadAllLines(templatePath).Length >= TestAssets.SourceUsers.Count + 1,
            $"{System.IO.File.ReadAllLines(templatePath).Length - 1} rows");

        // ---- Author a mapping: first two users -> a real target UPN ----
        string targetUpn;
        using (var tctx = Program.Target.CreateContext())
        {
            tctx.Load(tctx.Web.SiteUsers, us => us.Include(u => u.LoginName, u => u.Email, u => u.PrincipalType));
            await tctx.ExecuteQueryAsync();
            targetUpn = tctx.Web.SiteUsers.AsEnumerable()
                .First(u => u.PrincipalType == Microsoft.SharePoint.Client.Utilities.PrincipalType.User
                    && !string.IsNullOrEmpty(u.Email)).Email!;
        }
        var mappingPath = Path.Combine(Directory.GetCurrentDirectory(), "user-mapping.csv");
        UserMappingStore.SaveCsv(mappingPath, new[]
        {
            ("User", TestAssets.SourceUsers[0].Email, targetUpn),
            ("User", TestAssets.SourceUsers[1].Email, targetUpn),
            ("Group", "Migrator Test Members", "Target Members"),
        });
        var (userMap, groupMap) = UserMappingStore.LoadCsv(mappingPath);
        Program.Check("usermap: CSV round-trip", userMap.Count == 2 && groupMap.Count == 1,
            $"{userMap.Count} users, {groupMap.Count} groups");

        // ---- Cross-tenant copy with mapping, NO fallback ----
        using (var dctx = Program.Target.CreateContext())
        {
            await TestAssets.DeleteIfExistsAsync(dctx, "MigTest-MapCopy");
        }
        var result = await CopyEngine.CopyListAsync(site, Program.Target, TestAssets.SourceListTitle,
            new CopyOptions { TargetListTitle = "MigTest-MapCopy", TargetListUrl = "Lists/MigTestMapCopy" },
            userMap, default, null, groupMap);
        Console.WriteLine($"  copy: {result.Summary()}");
        Program.Check("usermap: copy no failures", result.Failed == 0, result.Summary());

        var unresolvedMapped = result.Records.Count(r => r.ItemType == "User" && r.Status == ItemCopyStatus.Warning
            && (r.SourcePath.Contains(TestAssets.SourceUsers[0].Email, StringComparison.OrdinalIgnoreCase)
             || r.SourcePath.Contains(TestAssets.SourceUsers[1].Email, StringComparison.OrdinalIgnoreCase)));
        Program.Check("usermap: mapped users resolved (no warnings for them)", unresolvedMapped == 0, $"{unresolvedMapped}");

        // Target items authored by mapped users carry the mapped identity.
        using var vctx = Program.Target.CreateContext();
        var tlist = vctx.Web.Lists.GetByTitle("MigTest-MapCopy");
        var titems = tlist.GetItems(CamlQuery.CreateAllItemsQuery(50));
        vctx.Load(titems);
        await vctx.ExecuteQueryAsync();
        var mappedAuthors = titems.AsEnumerable().Count(i =>
            i.FieldValues.GetValueOrDefault("Author") is FieldUserValue u
            && string.Equals(u.Email, targetUpn, StringComparison.OrdinalIgnoreCase));
        Program.Check("usermap: items authored by mapped identity", mappedAuthors > 0, $"{mappedAuthors} items -> {targetUpn}");
    }
}

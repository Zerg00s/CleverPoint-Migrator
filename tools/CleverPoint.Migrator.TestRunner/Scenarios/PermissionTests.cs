using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Item-level permission copy (behind CopyOptions.CopyPermissions): break
/// inheritance on two source items with distinct user+role assignments,
/// copy, and verify the target items carry equivalent unique permissions.
/// </summary>
public static class PermissionTests
{
    public static async Task RunAsync()
    {
        var site = Program.TestSite ?? await TestAssets.EnsureTestSiteAsync(Program.Source);
        Program.TestSite = site;
        if (TestAssets.SourceUsers.Count < 2)
        {
            Program.Check("perms: needs users", false, "not enough users");
            return;
        }

        // ---- Break inheritance on 2 source items ----
        using var ctx = site.CreateContext();
        var list = ctx.Web.Lists.GetByTitle(TestAssets.SourceListTitle);
        var items = list.GetItems(CamlQuery.CreateAllItemsQuery(10));
        ctx.Load(items, p => p.Include(i => i.Id, i => i.FileSystemObjectType, i => i.HasUniqueRoleAssignments));
        await ctx.ExecuteQueryAsync();
        var targets = items.AsEnumerable()
            .Where(i => i.FileSystemObjectType != FileSystemObjectType.Folder).Take(2).ToList();

        var roleDefs = ctx.Web.RoleDefinitions;
        ctx.Load(roleDefs, rds => rds.Include(rd => rd.Name));
        await ctx.ExecuteQueryAsync();
        var readDef = roleDefs.AsEnumerable().First(rd => rd.Name == "Read");
        var contribDef = roleDefs.AsEnumerable().FirstOrDefault(rd => rd.Name == "Contribute") ?? readDef;

        for (var i = 0; i < targets.Count; i++)
        {
            var item = targets[i];
            if (!item.HasUniqueRoleAssignments)
            {
                item.BreakRoleInheritance(false, false);
                await ctx.ExecuteQueryAsync();
            }
            var user = ctx.Web.EnsureUser(TestAssets.SourceUsers[i % TestAssets.SourceUsers.Count].Login);
            var bindings = new RoleDefinitionBindingCollection(ctx) { i == 0 ? readDef : contribDef };
            item.RoleAssignments.Add(user, bindings);
            await ctx.ExecuteQueryAsync();
        }
        Console.WriteLine($"  unique permissions set on items {string.Join(", ", targets.Select(t => t.Id))}");

        // ---- Copy with permissions enabled ----
        using (var dctx = site.CreateContext())
        {
            await TestAssets.DeleteIfExistsAsync(dctx, "MigTest-PermCopy");
        }
        var result = await CopyEngine.CopyListAsync(site, site, TestAssets.SourceListTitle, new CopyOptions
        {
            TargetListTitle = "MigTest-PermCopy",
            TargetListUrl = "Lists/MigTestPermCopy",
            CopyPermissions = true,
        });
        Console.WriteLine($"  copy: {result.Summary()}");
        foreach (var r in result.Records.Where(r => r.ItemType == "Permission"))
            Console.WriteLine($"    [{r.Status}] {r.SourcePath}: {r.Message}");
        Program.Check("perms: copy no failures", result.Failed == 0, result.Summary());
        Program.Check("perms: permission records present",
            result.Records.Count(r => r.ItemType == "Permission" && r.Status == ItemCopyStatus.Copied) == 2);

        // ---- Verify unique assignments on target ----
        using var tctx = site.CreateContext();
        var targetList = tctx.Web.Lists.GetByTitle("MigTest-PermCopy");
        var titems = targetList.GetItems(CamlQuery.CreateAllItemsQuery(50));
        tctx.Load(titems, p => p.Include(i => i.Id, i => i.HasUniqueRoleAssignments));
        await tctx.ExecuteQueryAsync();
        var uniqueOnTarget = titems.AsEnumerable().Where(i => i.HasUniqueRoleAssignments).ToList();
        Program.Check("perms: 2 target items have unique permissions", uniqueOnTarget.Count == 2, $"{uniqueOnTarget.Count}");

        if (uniqueOnTarget.Count > 0)
        {
            var first = uniqueOnTarget[0];
            tctx.Load(first.RoleAssignments, ras => ras.Include(
                ra => ra.Member.Title, ra => ra.RoleDefinitionBindings.Include(rd => rd.Name)));
            await tctx.ExecuteQueryAsync();
            var detail = string.Join("; ", first.RoleAssignments.AsEnumerable()
                .Select(ra => $"{ra.Member.Title}:{string.Join("+", ra.RoleDefinitionBindings.AsEnumerable().Select(rd => rd.Name))}"));
            var hasExpected = first.RoleAssignments.AsEnumerable().Any(ra =>
                ra.RoleDefinitionBindings.AsEnumerable().Any(rd => rd.Name is "Read" or "Contribute"));
            Program.Check("perms: assignment roles match", hasExpected, detail);
        }
    }
}

using CleverPoint.Migrator.Core.Auth;
using CleverPoint.Migrator.Core.Csom;
using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// The upsert map persists across runs (source item id -> target item id) so a re-copy UPDATES instead of
/// duplicating. But the target items it names can disappear -- someone deletes one, or the whole target
/// list is deleted and recreated. The map then points into nothing, and because item writes are BATCHED,
/// a single dead id fails every update queued alongside it:
///
///     Item does not exist. It may have been deleted by another user.
///
/// Two shapes, both asserted here:
///   1. the whole target list was recreated  -> the map is discarded wholesale;
///   2. individual target items were deleted -> just those mappings are pruned and the items re-added.
/// </summary>
public static class StaleUpsertMapTest
{
    private const string SourceSite = "https://gocleverpointcom.sharepoint.com/sites/DemoLargeSite";
    private const string TargetSite = "https://cleverpointlab.sharepoint.com/sites/intranet";
    private const string SrcList = "TaxMatrix-SrcList";
    private const string TargetList = "StaleUpsert-Target";

    public static async Task RunAsync()
    {
        var source = new SpConnection(SourceSite, new CertTokenProvider(Program.SourceCreds));
        var target = new SpConnection(TargetSite, new CertTokenProvider(Program.TargetCreds));

        using (var tctx = target.CreateContext())
            await TestAssets.DeleteIfExistsAsync(tctx, TargetList);

        CopyOptions NewOptions() => new()
        {
            TargetListTitle = TargetList,
            TargetListUrl = $"Lists/{TargetList}",
            CopyContent = true,
            CopyListSettings = true,
        };

        // ---- Run 1: fresh copy, and keep the map it produces (this is what history persists) ----
        var first = await CopyEngine.CopyListAsync(source, target, SrcList, NewOptions());
        Program.Check("stale-map: first copy succeeded", first.Failed == 0, $"{first.Failed} failure(s)");
        var map = first.ItemMappings.ToDictionary(m => m.SourceId, m => m.TargetId);
        Program.Check("stale-map: first copy produced an item map", map.Count > 0, $"{map.Count} mapping(s)");

        // ---- Case 1: the target LIST is deleted and recreated, but the caller still holds the map ----
        using (var tctx = target.CreateContext())
            await TestAssets.DeleteIfExistsAsync(tctx, TargetList);

        var afterRecreate = NewOptions();
        afterRecreate.UpsertItemMap = new Dictionary<int, int>(map);   // stale: points into the deleted list
        var second = await CopyEngine.CopyListAsync(source, target, SrcList, afterRecreate);

        foreach (var f in second.Records.Where(r => r.Status == ItemCopyStatus.Failed))
            Console.WriteLine($"    [FAILED] {f.ItemType} {f.SourcePath}: {f.Message}");
        Program.Check("stale-map: recreated list copies with NO failures", second.Failed == 0,
            $"{second.Failed} failure(s)");
        Program.Check("stale-map: recreated list re-added the items",
            second.ItemMappings.Count == map.Count, $"{second.ItemMappings.Count} item(s)");
        Program.Check("stale-map: the discarded map was reported",
            second.Records.Any(r => r.Message?.Contains("previous item mappings discarded") == true), "");

        await AssertItemCountAsync(target, map.Count, "after list recreate");

        // ---- Case 2: the list survives, but its items are deleted; the map still names them ----
        var liveMap = second.ItemMappings.ToDictionary(m => m.SourceId, m => m.TargetId);
        using (var tctx = target.CreateContext())
        {
            var list = tctx.Web.Lists.GetByTitle(TargetList);
            var items = list.GetItems(CamlQuery.CreateAllItemsQuery());
            tctx.Load(items);
            await tctx.ExecuteQueryAsync();
            foreach (var i in items.ToList()) i.DeleteObject();
            await tctx.ExecuteQueryAsync();
        }

        var afterItemDelete = NewOptions();
        afterItemDelete.UpsertItemMap = new Dictionary<int, int>(liveMap);   // stale: items gone, list alive
        var third = await CopyEngine.CopyListAsync(source, target, SrcList, afterItemDelete);

        foreach (var f in third.Records.Where(r => r.Status == ItemCopyStatus.Failed))
            Console.WriteLine($"    [FAILED] {f.ItemType} {f.SourcePath}: {f.Message}");
        Program.Check("stale-map: deleted target items copy with NO failures", third.Failed == 0,
            $"{third.Failed} failure(s)");
        Program.Check("stale-map: the pruned mappings were reported",
            third.Records.Any(r => r.Message?.Contains("no longer exist on the target") == true), "");

        await AssertItemCountAsync(target, map.Count, "after item delete");

        // ---- Control: a map that IS valid must still UPDATE, not duplicate ----
        var validMap = third.ItemMappings.ToDictionary(m => m.SourceId, m => m.TargetId);
        var fourth = NewOptions();
        fourth.UpsertItemMap = new Dictionary<int, int>(validMap);
        var run4 = await CopyEngine.CopyListAsync(source, target, SrcList, fourth);

        Program.Check("stale-map: a VALID map still updates in place (no duplicates)", run4.Failed == 0,
            $"{run4.Failed} failure(s)");
        Program.Check("stale-map: valid map produced updates, not adds",
            run4.Records.Any(r => r.Message == "updated (delta)"), "");
        await AssertItemCountAsync(target, map.Count, "after valid-map re-run");
    }

    private static async Task AssertItemCountAsync(SpConnection target, int expected, string when)
    {
        using var ctx = target.CreateContext();
        var list = ctx.Web.Lists.GetByTitle(TargetList);
        ctx.Load(list, l => l.ItemCount);
        await ctx.ExecuteQueryAsync();
        Program.Check($"stale-map: target has exactly {expected} item(s) {when}",
            list.ItemCount == expected, $"ItemCount={list.ItemCount}");
    }
}

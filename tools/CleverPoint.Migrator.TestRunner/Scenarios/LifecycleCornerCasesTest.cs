using CleverPoint.Migrator.Core.Auth;
using CleverPoint.Migrator.Core.Csom;
using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using Microsoft.SharePoint.Client;
using Microsoft.SharePoint.Client.Taxonomy;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// What happens when things get DELETED between runs. Migration is re-run constantly (deltas, retries,
/// fix-ups), and in between, people delete things. Each of these used to be a plausible way to get a
/// failed run, a duplicate, or a silently-empty column.
///
///   1. baseline copy (text + taxonomy metadata)
///   2. TARGET list deleted        -> re-copy re-adds everything, no duplicates, no failures
///   3. TARGET items deleted       -> re-copy re-adds them, no duplicates
///   4. TARGET column deleted      -> re-copy recreates the column AND restores its values
///   5. TARGET taxonomy column deleted -> recreated, rebound to the target term store, values restored
///   6. SOURCE column deleted      -> re-copy does not fail; the orphaned target column is simply not written
///   7. SOURCE items deleted       -> re-copy does not fail (a copy never deletes on the target)
///   8. SOURCE list deleted        -> the run fails with a CLEAR message, not a crash or a hang
///   9. SOURCE list recreated      -> copying works again from scratch
///
/// Runs cross-tenant, because that is the harder direction for every one of them.
/// </summary>
public static class LifecycleCornerCasesTest
{
    private const string SourceSite = "https://gocleverpointcom.sharepoint.com/sites/DemoLargeSite";
    private const string TargetSite = "https://cleverpointlab.sharepoint.com/sites/intranet";
    private const string SrcList = "Corner-Src";
    private const string TgtList = "Corner-Tgt";
    private const string TextCol = "CornerText";
    private const string TaxCol = "CornerTax";

    // The site-collection-scoped term set on the source ('One': Apple, Orange).
    private const string SiteTermSetId = "fa52e2d2-bcdf-40b3-ac61-8e067821786a";

    private static SpConnection _source = null!;
    private static SpConnection _target = null!;

    public static async Task RunAsync()
    {
        _source = new SpConnection(SourceSite, new CertTokenProvider(Program.SourceCreds));
        _target = new SpConnection(TargetSite, new CertTokenProvider(Program.TargetCreds));

        using (var tctx = _target.CreateContext())
            await TestAssets.DeleteIfExistsAsync(tctx, TgtList);

        var terms = await ProvisionSourceAsync();

        // ---- 1. Baseline ----
        var map = await CopyAsync("1. baseline", null, expectItems: 3);
        await VerifyValuesAsync("1. baseline", terms);

        // ---- 2. TARGET LIST deleted, caller still holds the map ----
        using (var tctx = _target.CreateContext())
            await TestAssets.DeleteIfExistsAsync(tctx, TgtList);
        map = await CopyAsync("2. target list deleted", map, expectItems: 3);
        await VerifyValuesAsync("2. target list deleted", terms);

        // ---- 3. TARGET ITEMS deleted, list survives ----
        await DeleteAllTargetItemsAsync();
        map = await CopyAsync("3. target items deleted", map, expectItems: 3);
        await VerifyValuesAsync("3. target items deleted", terms);

        // ---- 4. TARGET TEXT COLUMN deleted ----
        await DeleteTargetFieldAsync(TextCol);
        map = await CopyAsync("4. target text column deleted", map, expectItems: 3);
        Program.Check("corner [4]: target text column was recreated", await TargetHasFieldAsync(TextCol), TextCol);
        await VerifyValuesAsync("4. target text column deleted", terms);

        // ---- 5. TARGET TAXONOMY COLUMN deleted ----
        await DeleteTargetFieldAsync(TaxCol);
        map = await CopyAsync("5. target taxonomy column deleted", map, expectItems: 3);
        Program.Check("corner [5]: target taxonomy column was recreated", await TargetHasFieldAsync(TaxCol), TaxCol);
        await VerifyBindingAsync("5. target taxonomy column deleted");
        await VerifyValuesAsync("5. target taxonomy column deleted", terms);

        // ---- 6. SOURCE COLUMN deleted: the target keeps its (now orphaned) column, and nothing fails ----
        await DeleteSourceFieldAsync(TextCol);
        map = await CopyAsync("6. source text column deleted", map, expectItems: 3);
        Program.Check("corner [6]: orphaned target column still exists (a copy never drops target columns)",
            await TargetHasFieldAsync(TextCol), TextCol);
        // The taxonomy column is untouched by this, so its values must survive.
        await VerifyTaxonomyOnlyAsync("6. source text column deleted", terms);

        // ---- 7. SOURCE ITEMS deleted: a copy never deletes on the target ----
        await DeleteAllSourceItemsAsync();
        var afterSourceEmpty = await CopyRawAsync("7. source items deleted", map);
        Program.Check("corner [7]: copying an empty source does not fail", afterSourceEmpty.Failed == 0,
            $"{afterSourceEmpty.Failed} failure(s)");
        Program.Check("corner [7]: target items are NOT deleted when the source empties",
            await TargetItemCountAsync() == 3, $"{await TargetItemCountAsync()} item(s)");

        // ---- 8. SOURCE LIST deleted: fail clearly ----
        using (var sctx = _source.CreateContext())
            await TestAssets.DeleteIfExistsAsync(sctx, SrcList);

        Exception? thrown = null;
        try
        {
            await CopyEngine.CopyListAsync(_source, _target, SrcList, NewOptions(null));
        }
        catch (Exception ex)
        {
            thrown = ex;
        }
        Program.Check("corner [8]: a deleted SOURCE list fails the run (rather than silently copying nothing)",
            thrown != null, thrown?.GetType().Name ?? "<no exception>");
        Program.Check("corner [8]: the failure names the missing list",
            thrown != null && thrown.Message.Contains("List", StringComparison.OrdinalIgnoreCase),
            thrown?.Message ?? "");
        Program.Check("corner [8]: the target list was left intact",
            await TargetItemCountAsync() == 3, "target untouched");

        // ---- 9. SOURCE LIST recreated: back to normal ----
        var terms2 = await ProvisionSourceAsync();
        var final = await CopyRawAsync("9. source list recreated", null);
        Program.Check("corner [9]: copying a recreated source list succeeds", final.Failed == 0,
            $"{final.Failed} failure(s)");
        // The target already had 3 items and the map was dropped, so the 3 fresh source items are ADDED.
        Program.Check("corner [9]: recreated source items were added to the existing target",
            await TargetItemCountAsync() == 6, $"{await TargetItemCountAsync()} item(s)");
    }

    // ---------------- helpers ----------------

    private static CopyOptions NewOptions(Dictionary<int, int>? map) => new()
    {
        TargetListTitle = TgtList,
        TargetListUrl = $"Lists/{TgtList}",
        CopyContent = true,
        CopyListSettings = true,
        UpsertItemMap = map == null ? null : new Dictionary<int, int>(map),
    };

    /// <summary>Copies, asserts no failures, and returns the fresh upsert map.</summary>
    private static async Task<Dictionary<int, int>> CopyAsync(string step, Dictionary<int, int>? map, int expectItems)
    {
        var result = await CopyRawAsync(step, map);
        Program.Check($"corner [{step}]: copy produced no failures", result.Failed == 0, $"{result.Failed} failure(s)");
        var count = await TargetItemCountAsync();
        Program.Check($"corner [{step}]: target has exactly {expectItems} item(s) -- no duplicates",
            count == expectItems, $"ItemCount={count}");
        return result.ItemMappings.ToDictionary(m => m.SourceId, m => m.TargetId);
    }

    private static async Task<CopyResult> CopyRawAsync(string step, Dictionary<int, int>? map)
    {
        var result = await CopyEngine.CopyListAsync(_source, _target, SrcList, NewOptions(map));
        foreach (var f in result.Records.Where(r => r.Status == ItemCopyStatus.Failed))
            Console.WriteLine($"    [FAILED] [{step}] {f.ItemType} {f.SourcePath}: {f.Message}");
        return result;
    }

    /// <summary>Recreates the source list with a text column, a taxonomy column, and 3 tagged items.</summary>
    private static async Task<List<(string Label, Guid Id)>> ProvisionSourceAsync()
    {
        using var ctx = _source.CreateContext();
        var session = TaxonomySession.GetTaxonomySession(ctx);
        var store = session.GetDefaultSiteCollectionTermStore();
        ctx.Load(store, s => s.Id);
        await ctx.ExecuteQueryAsync();

        await TestAssets.DeleteIfExistsAsync(ctx, SrcList);

        var list = ctx.Web.Lists.Add(new ListCreationInformation
        {
            Title = SrcList, TemplateType = (int)ListTemplateType.GenericList, Url = $"Lists/{SrcList}",
        });
        ctx.Load(list, l => l.Id);
        await ctx.ExecuteQueryAsync();

        list.Fields.AddFieldAsXml(
            $"<Field Type='Text' DisplayName='{TextCol}' Name='{TextCol}' StaticName='{TextCol}' />",
            true, AddFieldOptions.AddFieldInternalNameHint);
        var taxField = list.Fields.AddFieldAsXml(
            $"<Field Type='TaxonomyFieldType' DisplayName='{TaxCol}' Name='{TaxCol}' StaticName='{TaxCol}' />",
            true, AddFieldOptions.AddFieldInternalNameHint);
        var tax = ctx.CastTo<TaxonomyField>(taxField);
        tax.SspId = store.Id;
        tax.TermSetId = Guid.Parse(SiteTermSetId);
        tax.Update();
        await ctx.ExecuteQueryAsync();

        var termSet = store.GetTermSet(Guid.Parse(SiteTermSetId));
        var allTerms = termSet.GetAllTerms();
        ctx.Load(allTerms, ts => ts.Include(t => t.Id, t => t.Name, t => t.PathOfTerm));
        await ctx.ExecuteQueryAsync();
        var terms = allTerms.AsEnumerable()
            .Where(t => !t.PathOfTerm.Contains(';'))
            .Select(t => (t.Name, t.Id)).ToList();

        for (var i = 1; i <= 3; i++)
        {
            var item = list.AddItem(new ListItemCreationInformation());
            item["Title"] = $"corner-{i}";
            item[TextCol] = $"text-{i}";
            var term = terms[(i - 1) % terms.Count];
            item[TaxCol] = new TaxonomyFieldValue
            {
                WssId = -1, Label = term.Item1, TermGuid = term.Item2.ToString(),
            };
            item.Update();
            await ctx.ExecuteQueryAsync();
        }
        Console.WriteLine($"  provisioned source '{SrcList}': 3 items, '{TextCol}' + '{TaxCol}' ({terms.Count} term(s))");
        return terms.Select(t => (t.Item1, t.Item2)).ToList();
    }

    private static async Task VerifyValuesAsync(string step, List<(string Label, Guid Id)> terms)
    {
        using var ctx = _target.CreateContext();
        var list = ctx.Web.Lists.GetByTitle(TgtList);
        var items = list.GetItems(CamlQuery.CreateAllItemsQuery());
        ctx.Load(items);
        await ctx.ExecuteQueryAsync();

        var text = items.AsEnumerable().Count(i => (i.FieldValues.GetValueOrDefault(TextCol) as string)?.StartsWith("text-") == true);
        var tagged = items.AsEnumerable().Count(i => i.FieldValues.GetValueOrDefault(TaxCol) is TaxonomyFieldValue v
                                                     && !string.IsNullOrEmpty(v.TermGuid) && v.WssId > 0);
        Program.Check($"corner [{step}]: all 3 items carry their text value", text == 3, $"{text}/3");
        Program.Check($"corner [{step}]: all 3 items carry a resolved taxonomy value", tagged == 3, $"{tagged}/3");
    }

    private static async Task VerifyTaxonomyOnlyAsync(string step, List<(string Label, Guid Id)> terms)
    {
        using var ctx = _target.CreateContext();
        var list = ctx.Web.Lists.GetByTitle(TgtList);
        var items = list.GetItems(CamlQuery.CreateAllItemsQuery());
        ctx.Load(items);
        await ctx.ExecuteQueryAsync();
        var tagged = items.AsEnumerable().Count(i => i.FieldValues.GetValueOrDefault(TaxCol) is TaxonomyFieldValue v
                                                     && !string.IsNullOrEmpty(v.TermGuid) && v.WssId > 0);
        Program.Check($"corner [{step}]: taxonomy values survived", tagged == 3, $"{tagged}/3");
    }

    private static async Task VerifyBindingAsync(string step)
    {
        using var ctx = _target.CreateContext();
        var session = TaxonomySession.GetTaxonomySession(ctx);
        var store = session.GetDefaultSiteCollectionTermStore();
        ctx.Load(store, s => s.Id);
        var list = ctx.Web.Lists.GetByTitle(TgtList);
        var tax = ctx.CastTo<TaxonomyField>(list.Fields.GetByInternalNameOrTitle(TaxCol));
        ctx.Load(tax, f => f.SspId, f => f.TermSetId);
        await ctx.ExecuteQueryAsync();

        Program.Check($"corner [{step}]: recreated column is bound to the TARGET term store",
            tax.SspId == store.Id, $"SspId={tax.SspId} store={store.Id}");

        var ts = store.GetTermSet(tax.TermSetId);
        ctx.Load(ts, t => t.Name);
        await ctx.ExecuteQueryAsync();
        Program.Check($"corner [{step}]: recreated column's term set resolves on the target",
            ts.ServerObjectIsNull != true, $"{tax.TermSetId}");
    }

    private static async Task<int> TargetItemCountAsync()
    {
        using var ctx = _target.CreateContext();
        var list = ctx.Web.Lists.GetByTitle(TgtList);
        ctx.Load(list, l => l.ItemCount);
        await ctx.ExecuteQueryAsync();
        return list.ItemCount;
    }

    private static async Task<bool> TargetHasFieldAsync(string internalName)
    {
        using var ctx = _target.CreateContext();
        var list = ctx.Web.Lists.GetByTitle(TgtList);
        ctx.Load(list, l => l.Fields.Include(f => f.InternalName));
        await ctx.ExecuteQueryAsync();
        return list.Fields.AsEnumerable().Any(f =>
            string.Equals(f.InternalName, internalName, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task DeleteTargetFieldAsync(string internalName)
    {
        using var ctx = _target.CreateContext();
        var list = ctx.Web.Lists.GetByTitle(TgtList);
        var field = list.Fields.GetByInternalNameOrTitle(internalName);
        field.DeleteObject();
        await ctx.ExecuteQueryAsync();
        Console.WriteLine($"    deleted TARGET column '{internalName}'");
    }

    private static async Task DeleteSourceFieldAsync(string internalName)
    {
        using var ctx = _source.CreateContext();
        var list = ctx.Web.Lists.GetByTitle(SrcList);
        var field = list.Fields.GetByInternalNameOrTitle(internalName);
        field.DeleteObject();
        await ctx.ExecuteQueryAsync();
        Console.WriteLine($"    deleted SOURCE column '{internalName}'");
    }

    private static async Task DeleteAllTargetItemsAsync()
    {
        using var ctx = _target.CreateContext();
        var list = ctx.Web.Lists.GetByTitle(TgtList);
        var items = list.GetItems(CamlQuery.CreateAllItemsQuery());
        ctx.Load(items);
        await ctx.ExecuteQueryAsync();
        foreach (var i in items.ToList()) i.DeleteObject();
        await ctx.ExecuteQueryAsync();
        Console.WriteLine("    deleted ALL TARGET items");
    }

    private static async Task DeleteAllSourceItemsAsync()
    {
        using var ctx = _source.CreateContext();
        var list = ctx.Web.Lists.GetByTitle(SrcList);
        var items = list.GetItems(CamlQuery.CreateAllItemsQuery());
        ctx.Load(items);
        await ctx.ExecuteQueryAsync();
        foreach (var i in items.ToList()) i.DeleteObject();
        await ctx.ExecuteQueryAsync();
        Console.WriteLine("    deleted ALL SOURCE items");
    }
}

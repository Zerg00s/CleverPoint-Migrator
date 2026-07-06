using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Reproduces/verifies review finding M1: when a CSOM batch stops at the first server
/// error, the items queued BEFORE it already committed on the target, but the whole batch
/// was recorded Failed. That made resume/healing re-add the committed items as duplicates.
///
/// A target column-validation rule (Num &lt;= 100) rejects one poison item mid-batch, so a
/// few items commit before it and the rest do not.
///
/// BEFORE the fix: target has committed items, but 0 are recorded Copied (all Failed).
/// AFTER the fix: the committed items are recorded Copied (with id maps); only the poison
/// and the unreached items are Failed.
/// </summary>
public static class BatchPartialTest
{
    private const string SrcTitle = "MigTest-M1-Src";
    private const string TgtTitle = "MigTest-M1-Tgt";

    public static async Task RunAsync()
    {
        var site = Program.TestSite ?? await TestAssets.EnsureTestSiteAsync(Program.Source);
        Program.TestSite = site;

        var nums = new[] { 10, 20, 30, 999, 50, 60 };   // 999 is the poison (violates Num<=100)

        // The migtest site has retention, so lists can't be deleted between runs. Make the
        // setup idempotent: create lists + columns only if missing, then clear items each run.
        using (var ctx = site.CreateContext())
        {
            var lists = ctx.Web.Lists;
            ctx.Load(lists, ls => ls.Include(l => l.Title));
            await ctx.ExecuteQueryAsync();
            var have = lists.AsEnumerable().Select(l => l.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (!have.Contains(SrcTitle))
            {
                ctx.Web.Lists.Add(new ListCreationInformation { Title = SrcTitle, TemplateType = (int)ListTemplateType.GenericList, Url = "Lists/MigTestM1Src" });
                await ctx.ExecuteQueryAsync();
                ctx.Web.Lists.GetByTitle(SrcTitle).Fields.AddFieldAsXml("<Field Type='Number' DisplayName='Num' Name='Num'/>", true, AddFieldOptions.DefaultValue);
                await ctx.ExecuteQueryAsync();
            }
            if (!have.Contains(TgtTitle))
            {
                ctx.Web.Lists.Add(new ListCreationInformation { Title = TgtTitle, TemplateType = (int)ListTemplateType.GenericList, Url = "Lists/MigTestM1Tgt" });
                await ctx.ExecuteQueryAsync();
                // Target has the same column, but with a validation rule that rejects Num > 100.
                ctx.Web.Lists.GetByTitle(TgtTitle).Fields.AddFieldAsXml("<Field Type='Number' DisplayName='Num' Name='Num'><Validation Message='max 100'>=Num&lt;=100</Validation></Field>", true, AddFieldOptions.DefaultValue);
                await ctx.ExecuteQueryAsync();
            }

            await ClearItemsAsync(ctx, SrcTitle);
            await ClearItemsAsync(ctx, TgtTitle);

            var src = ctx.Web.Lists.GetByTitle(SrcTitle);
            for (var i = 0; i < nums.Length; i++)
            {
                var it = src.AddItem(new ListItemCreationInformation());
                it["Title"] = $"row{i + 1}";
                it["Num"] = nums[i];
                it.Update();
            }
            await ctx.ExecuteQueryAsync();
        }

        // Content-only copy so the target's validation column is left in place.
        var res = await CopyEngine.CopyListAsync(site, site, SrcTitle, new CopyOptions
        {
            TargetListTitle = TgtTitle,
            MergeSchema = false,
            CopyViews = false,
            CopyListSettings = false,
        });

        var copied = res.Records.Count(r => r.ItemType == "Item" && r.Status == ItemCopyStatus.Copied);
        var failed = res.Records.Count(r => r.ItemType == "Item" && r.Status == ItemCopyStatus.Failed);

        long tgtCount;
        using (var ctx = site.CreateContext())
        {
            var l = ctx.Web.Lists.GetByTitle(TgtTitle);
            ctx.Load(l, x => x.ItemCount);
            await ctx.ExecuteQueryAsync();
            tgtCount = l.ItemCount;
        }
        Console.WriteLine($"  target items actually committed: {tgtCount}; recorded Copied={copied}, Failed={failed}");

        // Setup sanity: some items must have committed AND the batch must have hit the poison.
        Program.Check("m1: partial commit occurred (setup)", tgtCount > 0 && failed > 0, $"committed={tgtCount}, failed={failed}");

        // THE FIX: every item that actually committed is recorded Copied (not Failed). This
        // FAILS before the fix (all recorded Failed, copied=0) and PASSES after.
        Program.Check("m1: committed items recorded Copied, not Failed (FAIL = bug)",
            copied == tgtCount && copied > 0, $"recorded Copied={copied}, target has {tgtCount}");
    }

    private static async Task ClearItemsAsync(ClientContext ctx, string listTitle)
    {
        var list = ctx.Web.Lists.GetByTitle(listTitle);
        var items = list.GetItems(CamlQuery.CreateAllItemsQuery());
        ctx.Load(items);
        await ctx.ExecuteQueryAsync();
        foreach (var it in items.ToList()) it.DeleteObject();
        await ctx.ExecuteQueryAsync();
    }
}

using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Content-only copies must leave the target schema COMPLETELY untouched:
/// no new fields, no views, no formatter syncs. Items land only where the
/// target already has matching columns.
/// </summary>
public static class ContentOnlyTest
{
    public static async Task RunAsync()
    {
        var site = Program.TestSite ?? Program.Source.ForWeb($"{Program.Source.SiteUrl}/migtest");
        using (var ctx = site.CreateContext())
        {
            await TestAssets.DeleteIfExistsAsync(ctx, "MigTest-ContentOnly");
            // Retention may block the delete (clear+reuse tenant): re-create
            // only when truly gone, otherwise reuse the cleared list.
            var lists = ctx.Web.Lists;
            ctx.Load(lists, ls => ls.Include(l => l.Title));
            await ctx.ExecuteQueryAsync();
            if (!lists.AsEnumerable().Any(l => l.Title.Equals("MigTest-ContentOnly", StringComparison.OrdinalIgnoreCase)))
            {
                var list = ctx.Web.Lists.Add(new ListCreationInformation
                {
                    Title = "MigTest-ContentOnly", TemplateType = 100, Url = "Lists/MigTestContentOnly",
                });
                list.Fields.AddFieldAsXml(
                    "<Field Type='Text' DisplayName='SentinelCol' Name='SentinelCol' StaticName='SentinelCol'/>",
                    true, AddFieldOptions.AddFieldToDefaultView);
                await ctx.ExecuteQueryAsync();
            }
        }

        int fieldsBefore;
        using (var ctx = site.CreateContext())
        {
            var list = ctx.Web.Lists.GetByTitle("MigTest-ContentOnly");
            ctx.Load(list, l => l.Fields.Include(f => f.InternalName), l => l.Views.Include(v => v.Title));
            await ctx.ExecuteQueryAsync();
            fieldsBefore = list.Fields.Count;
        }

        var options = new CopyOptions
        {
            TargetListTitle = "MigTest-ContentOnly",
            MergeSchema = false, CopyViews = false, CopyListSettings = false,
        };
        var result = await CopyEngine.CopyListAsync(site, site, TestAssets.SourceListTitle, options);
        Console.WriteLine($"  copy result: {result.Summary()}");
        Program.Check("content-only: no failures", result.Failed == 0, result.Summary());
        var itemsCopied = result.Records.Count(r => r.ItemType == "Item" && r.Status == ItemCopyStatus.Copied);
        Program.Check("content-only: items copied into the bare list", itemsCopied >= 20, $"{itemsCopied} items");

        using (var ctx = site.CreateContext())
        {
            var list = ctx.Web.Lists.GetByTitle("MigTest-ContentOnly");
            ctx.Load(list, l => l.Fields.Include(f => f.InternalName));
            await ctx.ExecuteQueryAsync();
            var fieldsAfter = list.Fields.Count;
            var sentinel = list.Fields.AsEnumerable().Any(f => f.InternalName == "SentinelCol");
            var leaked = list.Fields.AsEnumerable().Any(f => f.InternalName == "TextCol");
            Program.Check("content-only: target schema untouched (field count unchanged)",
                fieldsAfter == fieldsBefore, $"{fieldsBefore} before, {fieldsAfter} after");
            Program.Check("content-only: sentinel column survived", sentinel);
            Program.Check("content-only: no source columns leaked in", !leaked);
        }
    }
}

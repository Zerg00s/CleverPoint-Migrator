using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Verifies review finding H11: a content-only copy (MergeSchema=false) into a target list
/// that lacks a source column drops that column's values. It must no longer be silent, a
/// Warning is recorded naming the dropped column(s).
/// </summary>
public static class ContentMetaWarnTest
{
    private const string SrcTitle = "MigTest-H11-Src";
    private const string TgtTitle = "MigTest-H11-Tgt";

    public static async Task RunAsync()
    {
        var site = Program.TestSite ?? await TestAssets.EnsureTestSiteAsync(Program.Source);
        Program.TestSite = site;

        using (var ctx = site.CreateContext())
        {
            await TestAssets.DeleteIfExistsAsync(ctx, SrcTitle);
            await TestAssets.DeleteIfExistsAsync(ctx, TgtTitle);
            // Source list WITH a custom column "Extra".
            ctx.Web.Lists.Add(new ListCreationInformation { Title = SrcTitle, TemplateType = (int)ListTemplateType.GenericList, Url = "Lists/MigTestH11Src" });
            // Target list WITHOUT it (exists already, so the copy is content-only).
            ctx.Web.Lists.Add(new ListCreationInformation { Title = TgtTitle, TemplateType = (int)ListTemplateType.GenericList, Url = "Lists/MigTestH11Tgt" });
            await ctx.ExecuteQueryAsync();
            var src = ctx.Web.Lists.GetByTitle(SrcTitle);
            src.Fields.AddFieldAsXml("<Field Type='Text' DisplayName='Extra' Name='Extra'/>", true, AddFieldOptions.DefaultValue);
            await ctx.ExecuteQueryAsync();
            var it = src.AddItem(new ListItemCreationInformation());
            it["Title"] = "row1";
            it["Extra"] = "keep-me";
            it.Update();
            await ctx.ExecuteQueryAsync();
        }

        // Content-only copy (schema NOT merged), so the target keeps its columns as-is.
        var res = await CopyEngine.CopyListAsync(site, site, SrcTitle, new CopyOptions
        {
            TargetListTitle = TgtTitle,
            MergeSchema = false,
            CopyViews = false,
            CopyListSettings = false,
        });

        var warn = res.Records.FirstOrDefault(r => r.Status == ItemCopyStatus.Warning
            && (r.Message?.Contains("not on the target") ?? false));
        Console.WriteLine($"  warning: {warn?.Message}");
        Program.Check("h11: dropped-column warning is recorded", warn != null && (warn.Message?.Contains("Extra") ?? false),
            warn?.Message ?? "no warning record");
        Program.Check("h11: the item still copied (content intact)",
            res.Records.Any(r => r.ItemType == "Item" && r.Status == ItemCopyStatus.Copied), res.Summary());
    }
}

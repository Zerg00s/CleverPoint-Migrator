using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Schema dependency auto-copy: a site column + content type on the migtest
/// subsite web, attached to a list with items. Copying that list CROSS-SITE
/// (to the DemoLargeSite parent web) must create the site column and content
/// type there automatically, attach the CT to the target list, assign it to
/// items, and a delta re-run must treat everything as already-present.
/// </summary>
public static class ContentTypeTests
{
    private const string SiteColName = "MigSiteCol";
    private const string CtName = "MigTest Invoice";
    private const string ListTitle = "MigTest-CTList";
    private const string CopyTitle = "MigTest-CTCopy";

    public static async Task RunAsync()
    {
        var site = Program.TestSite ?? await TestAssets.EnsureTestSiteAsync(Program.Source);
        Program.TestSite = site;

        // ---- Provision: site column + CT + list on the SUBSITE web ----
        using var ctx = site.CreateContext();
        var webFields = ctx.Web.Fields;
        ctx.Load(webFields, fs => fs.Include(f => f.InternalName));
        var webCts = ctx.Web.ContentTypes;
        ctx.Load(webCts, cts => cts.Include(ct => ct.Name, ct => ct.StringId));
        await ctx.ExecuteQueryAsync();

        if (!webFields.AsEnumerable().Any(f => f.InternalName == SiteColName))
        {
            ctx.Web.Fields.AddFieldAsXml(
                $"<Field Type='Text' Name='{SiteColName}' DisplayName='Mig Site Col' Group='CleverPoint Test Columns' MaxLength='120' />",
                false, AddFieldOptions.AddFieldInternalNameHint);
            await ctx.ExecuteQueryAsync();
        }

        var ct = webCts.AsEnumerable().FirstOrDefault(c => c.Name == CtName);
        if (ct == null)
        {
            var created = ctx.Web.ContentTypes.Add(new ContentTypeCreationInformation
            {
                Name = CtName,
                Group = "CleverPoint Test CTs",
                ParentContentType = null,
                Id = "0x0100A1B2C3D4E5F60718293A4B5C6D7E8F90",
            });
            var field = ctx.Web.AvailableFields.GetByInternalNameOrTitle(SiteColName);
            created.FieldLinks.Add(new FieldLinkCreationInformation { Field = field });
            created.Update(false);
            await ctx.ExecuteQueryAsync();
        }

        var deleted = await TestAssets.DeleteIfExistsAsync(ctx, ListTitle);
        List list;
        if (deleted)
        {
            list = ctx.Web.Lists.Add(new ListCreationInformation
            {
                Title = ListTitle,
                TemplateType = (int)ListTemplateType.GenericList,
                Url = "Lists/MigTestCTList",
            });
            list.ContentTypesEnabled = true;
            list.Update();
            await ctx.ExecuteQueryAsync();
            var webCt = ctx.Web.AvailableContentTypes;
            ctx.Load(webCt, cts => cts.Include(c => c.Name, c => c.StringId));
            await ctx.ExecuteQueryAsync();
            list.ContentTypes.AddExistingContentType(
                ctx.Web.AvailableContentTypes.AsEnumerable().First(c => c.Name == CtName));
            await ctx.ExecuteQueryAsync();
        }
        else
        {
            list = ctx.Web.Lists.GetByTitle(ListTitle);
        }

        // Items using the CT.
        ctx.Load(list.ContentTypes, cts => cts.Include(c => c.Name, c => c.StringId));
        await ctx.ExecuteQueryAsync();
        var listCtId = list.ContentTypes.AsEnumerable().First(c => c.Name == CtName).StringId;
        for (var i = 0; i < 5; i++)
        {
            var item = list.AddItem(new ListItemCreationInformation());
            item["Title"] = $"Invoice {i + 1:D2}";
            item[SiteColName] = $"value-{i + 1}";
            item["ContentTypeId"] = listCtId;
            item.Update();
        }
        await ctx.ExecuteQueryAsync();

        // ---- Copy cross-site (parent web has neither the column nor the CT) ----
        using (var pctx = Program.Source.CreateContext())
        {
            await TestAssets.DeleteIfExistsAsync(pctx, CopyTitle);
        }
        var options = new CopyOptions { TargetListTitle = CopyTitle, TargetListUrl = "Lists/MigTestCTCopy" };
        var result = await CopyEngine.CopyListAsync(site, Program.Source, ListTitle, options);
        Console.WriteLine($"  copy: {result.Summary()}");
        foreach (var r in result.Records.Where(r => r.ItemType is "SiteColumn" or "ContentType"))
            Console.WriteLine($"    [{r.Status}] {r.ItemType} {r.SourcePath}: {r.Message}");
        Program.Check("ctypes: copy no failures", result.Failed == 0, result.Summary());
        Program.Check("ctypes: site column dependency copied",
            result.Records.Any(r => r.ItemType == "SiteColumn" && r.SourcePath == SiteColName && r.Status is ItemCopyStatus.Copied or ItemCopyStatus.Skipped));
        Program.Check("ctypes: content type dependency copied",
            result.Records.Any(r => r.ItemType == "ContentType" && r.SourcePath == CtName && r.Status is ItemCopyStatus.Copied or ItemCopyStatus.Skipped));

        // ---- Verify on the target ----
        using var tctx = Program.Source.CreateContext();
        var targetList = tctx.Web.Lists.GetByTitle(CopyTitle);
        tctx.Load(targetList.ContentTypes, cts => cts.Include(c => c.Name));
        var titems = targetList.GetItems(CamlQuery.CreateAllItemsQuery(20));
        tctx.Load(titems);
        await tctx.ExecuteQueryAsync();
        Program.Check("ctypes: CT attached to target list",
            targetList.ContentTypes.AsEnumerable().Any(c => c.Name == CtName));
        var ctAssigned = titems.AsEnumerable().Count(i =>
            i.FieldValues.GetValueOrDefault("ContentTypeId")?.ToString()?.StartsWith("0x0100A1B2C3D4E5F60718293A4B5C6D7E8F90", StringComparison.OrdinalIgnoreCase) == true);
        var valuesOk = titems.AsEnumerable().Count(i => (i.FieldValues.GetValueOrDefault(SiteColName)?.ToString() ?? "").StartsWith("value-"));
        Program.Check("ctypes: items carry the content type", ctAssigned == 5, $"{ctAssigned}/5");
        Program.Check("ctypes: site column values copied", valuesOk == 5, $"{valuesOk}/5");

        // ---- Delta re-run: dependencies must be graceful no-ops ----
        var result2 = await CopyEngine.CopyListAsync(site, Program.Source, ListTitle, new CopyOptions
        {
            TargetListTitle = CopyTitle,
            ModifiedSinceUtc = result.MaxSourceModifiedUtc!.Value.AddSeconds(1),
        });
        var depFailures = result2.Records.Count(r => r.ItemType is "SiteColumn" or "ContentType" && r.Status == ItemCopyStatus.Failed);
        Program.Check("ctypes: delta re-run dependencies graceful", result2.Failed == 0 && depFailures == 0, result2.Summary());
    }
}

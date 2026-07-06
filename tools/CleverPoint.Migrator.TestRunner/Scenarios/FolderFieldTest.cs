using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Reproduces/verifies review finding M6: folder list items lose their custom column values
/// on copy (only Title/authors/dates were copied). A library folder with a "Dept" value must
/// arrive on the target with that value, not blank.
///
/// BEFORE the fix: target folder's Dept is empty. AFTER: it is "Sales".
/// </summary>
public static class FolderFieldTest
{
    private const string SrcTitle = "MigTest-M6-Src";
    private const string TgtTitle = "MigTest-M6-Tgt";
    private const string FolderName = "DeptFolder";

    public static async Task RunAsync()
    {
        var site = Program.TestSite ?? await TestAssets.EnsureTestSiteAsync(Program.Source);
        Program.TestSite = site;

        string webUrl, srcRoot, tgtRoot;
        using (var ctx = site.CreateContext())
        {
            ctx.Load(ctx.Web, w => w.Url);
            var lists = ctx.Web.Lists;
            ctx.Load(lists, ls => ls.Include(l => l.Title));
            await ctx.ExecuteQueryAsync();
            webUrl = ctx.Web.Url.TrimEnd('/');
            var have = lists.AsEnumerable().Select(l => l.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var (title, url) in new[] { (SrcTitle, "MigTestM6Src"), (TgtTitle, "MigTestM6Tgt") })
                if (!have.Contains(title))
                {
                    ctx.Web.Lists.Add(new ListCreationInformation { Title = title, TemplateType = (int)ListTemplateType.DocumentLibrary, Url = url });
                    await ctx.ExecuteQueryAsync();
                    ctx.Web.Lists.GetByTitle(title).Fields.AddFieldAsXml("<Field Type='Text' DisplayName='Dept' Name='Dept'/>", true, AddFieldOptions.AddToDefaultContentType);
                    await ctx.ExecuteQueryAsync();
                }

            var src = ctx.Web.Lists.GetByTitle(SrcTitle);
            var tgt = ctx.Web.Lists.GetByTitle(TgtTitle);
            ctx.Load(src.RootFolder, f => f.ServerRelativeUrl);
            ctx.Load(tgt.RootFolder, f => f.ServerRelativeUrl);
            await ctx.ExecuteQueryAsync();
            srcRoot = src.RootFolder.ServerRelativeUrl;
            tgtRoot = tgt.RootFolder.ServerRelativeUrl;

            src.RootFolder.Folders.Add(FolderName);
            await ctx.ExecuteQueryAsync();
        }

        // Set the source folder's Dept value.
        await SetFolderDeptAsync(site, $"{srcRoot}/{FolderName}", "Sales");
        // Clear any leftover target value from a prior run so we measure THIS copy.
        try { await SetFolderDeptAsync(site, $"{tgtRoot}/{FolderName}", ""); } catch { /* target folder may not exist yet */ }
        Program.Check("m6: source folder Dept set (setup)", await GetFolderDeptAsync(site, $"{srcRoot}/{FolderName}") == "Sales", "Sales");

        // Full copy (schema merged so the target has the Dept column).
        var res = await CopyEngine.CopyListAsync(site, site, SrcTitle, new CopyOptions { TargetListTitle = TgtTitle });
        Program.Check("m6: copy had no failures (setup)", res.Failed == 0, res.Summary());

        var tgtDept = await GetFolderDeptAsync(site, $"{tgtRoot}/{FolderName}");
        Console.WriteLine($"  target folder Dept = '{tgtDept}' (want 'Sales')");
        Program.Check("m6: folder custom field copied (FAIL = bug)", tgtDept == "Sales", $"target Dept='{tgtDept}'");
    }

    private static async Task SetFolderDeptAsync(Core.Csom.SpConnection site, string folderServerRel, string value)
    {
        using var ctx = site.CreateContext();
        var item = ctx.Web.GetFolderByServerRelativeUrl(folderServerRel).ListItemAllFields;
        item["Dept"] = value;
        item.UpdateOverwriteVersion();
        await ctx.ExecuteQueryAsync();
    }

    private static async Task<string> GetFolderDeptAsync(Core.Csom.SpConnection site, string folderServerRel)
    {
        using var ctx = site.CreateContext();
        var item = ctx.Web.GetFolderByServerRelativeUrl(folderServerRel).ListItemAllFields;
        ctx.Load(item, i => i["Dept"]);
        await ctx.ExecuteQueryAsync();
        return item["Dept"]?.ToString() ?? "";
    }
}

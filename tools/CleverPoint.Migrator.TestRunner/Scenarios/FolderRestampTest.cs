using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Verifies the folder-date re-stamp: copying files INTO a folder re-stamps that folder's
/// Modified to the copy time, clobbering the source date set during the folder pass. The engine
/// re-applies each folder's dates in a final pass once its contents are in, so a copied folder
/// keeps its ORIGINAL Created/Modified rather than showing "now".
///
/// Expected AFTER the fix: the target folder's Modified matches the (old) source date, not today.
/// </summary>
public static class FolderRestampTest
{
    private const string SrcTitle = "MigTest-Restamp-Src";
    private const string TgtTitle = "MigTest-Restamp-Tgt";

    public static async Task RunAsync()
    {
        var site = Program.TestSite ?? await TestAssets.EnsureTestSiteAsync(Program.Source);
        Program.TestSite = site;

        var oldModified = new DateTime(2019, 6, 15, 8, 30, 0, DateTimeKind.Utc);
        var oldCreated = new DateTime(2018, 3, 2, 10, 0, 0, DateTimeKind.Utc);

        string webUrl, srcRoot;
        using (var ctx = site.CreateContext())
        {
            ctx.Load(ctx.Web, w => w.Url);
            var lists = ctx.Web.Lists;
            ctx.Load(lists, ls => ls.Include(l => l.Title));
            await ctx.ExecuteQueryAsync();
            webUrl = ctx.Web.Url.TrimEnd('/');
            if (!lists.AsEnumerable().Any(l => l.Title == SrcTitle))
            {
                ctx.Web.Lists.Add(new ListCreationInformation
                {
                    Title = SrcTitle,
                    TemplateType = (int)ListTemplateType.DocumentLibrary,
                    Url = "MigTestRestampSrc",
                });
                await ctx.ExecuteQueryAsync();
            }
            var lib = ctx.Web.Lists.GetByTitle(SrcTitle);
            ctx.Load(lib.RootFolder, f => f.ServerRelativeUrl);
            await ctx.ExecuteQueryAsync();
            srcRoot = lib.RootFolder.ServerRelativeUrl;
            lib.RootFolder.Folders.Add("DatedFolder");
            await ctx.ExecuteQueryAsync();
        }

        // Put several files INSIDE the folder (each add bumps the folder's Modified to now).
        var rnd = new Random(7);
        for (var i = 0; i < 4; i++)
        {
            var buf = new byte[256];
            rnd.NextBytes(buf);
            await site.Rest.PostBinaryAsync(
                $"{webUrl}/_api/web/GetFolderByServerRelativeUrl('{srcRoot.Replace("'", "''")}/DatedFolder')/Files/add(url='f{i}.bin',overwrite=true)",
                buf, buf.Length);
        }

        // Back-date the FOLDER last, so the source folder carries the old dates at scan time.
        using (var ctx = site.CreateContext())
        {
            var folderItem = ctx.Web.GetFolderByServerRelativeUrl($"{srcRoot}/DatedFolder").ListItemAllFields;
            folderItem["Created"] = oldCreated;
            folderItem["Modified"] = oldModified;
            folderItem.UpdateOverwriteVersion();
            await ctx.ExecuteQueryAsync();
        }

        // ---- Copy the library (PreserveAuthorsAndDates is on by default) ----
        using (var ctx = site.CreateContext()) await TestAssets.DeleteIfExistsAsync(ctx, TgtTitle);
        var res = await CopyEngine.CopyListAsync(site, site, SrcTitle, new CopyOptions
        {
            TargetListTitle = TgtTitle,
            TargetListUrl = "MigTestRestampTgt",
        });
        Program.Check("restamp: copy had no failures", res.Failed == 0, res.Summary());

        // Read the TARGET folder's dates back.
        DateTime tgtModified = DateTime.MaxValue, tgtCreated = DateTime.MaxValue;
        using (var ctx = site.CreateContext())
        {
            var tgt = ctx.Web.Lists.GetByTitle(TgtTitle);
            ctx.Load(tgt.RootFolder, f => f.ServerRelativeUrl);
            await ctx.ExecuteQueryAsync();
            var item = ctx.Web.GetFolderByServerRelativeUrl($"{tgt.RootFolder.ServerRelativeUrl}/DatedFolder").ListItemAllFields;
            ctx.Load(item, i => i["Modified"], i => i["Created"]);
            await ctx.ExecuteQueryAsync();
            if (item["Modified"] is DateTime m) tgtModified = m.ToUniversalTime();
            if (item["Created"] is DateTime c) tgtCreated = c.ToUniversalTime();
        }

        var modOff = Math.Abs((tgtModified - oldModified).TotalMinutes);
        var creOff = Math.Abs((tgtCreated - oldCreated).TotalMinutes);
        Console.WriteLine($"  target folder Modified={tgtModified:yyyy-MM-dd HH:mm}Z (want {oldModified:yyyy-MM-dd HH:mm}Z, off {modOff:F0} min)");
        Console.WriteLine($"  target folder Created ={tgtCreated:yyyy-MM-dd HH:mm}Z (want {oldCreated:yyyy-MM-dd HH:mm}Z, off {creOff:F0} min)");

        // The whole point: after files landed inside, the folder still shows its ORIGINAL date.
        Program.Check("restamp: target folder Modified matches source (not the copy time)", modOff <= 2,
            $"target={tgtModified:yyyy-MM-dd HH:mm}Z source={oldModified:yyyy-MM-dd HH:mm}Z");
        Program.Check("restamp: target folder Created matches source", creOff <= 2,
            $"target={tgtCreated:yyyy-MM-dd HH:mm}Z source={oldCreated:yyyy-MM-dd HH:mm}Z");
    }
}

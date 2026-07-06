using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Reproduces/verifies review finding H4: a date filter must NOT drop folders.
/// A folder whose Modified predates the ModifiedSince cutoff was being filtered
/// out of the scan, so a recent file inside it had no parent to land in and failed.
/// After the fix folders always pass the date filter, so the recent file copies.
///
/// Expected AFTER the fix: the recent file inside the old folder is Copied.
/// (Before the fix it was Failed with "folder does not exist".)
/// </summary>
public static class DateFolderTest
{
    private const string SrcTitle = "MigTest-H4-Src";
    private const string TgtTitle = "MigTest-H4-Tgt";

    public static async Task RunAsync()
    {
        var site = Program.TestSite ?? await TestAssets.EnsureTestSiteAsync(Program.Source);
        Program.TestSite = site;

        var cutoff = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var oldDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

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
                    Url = "MigTestH4Src",
                });
                await ctx.ExecuteQueryAsync();
            }
            var lib = ctx.Web.Lists.GetByTitle(SrcTitle);
            ctx.Load(lib.RootFolder, f => f.ServerRelativeUrl);
            await ctx.ExecuteQueryAsync();
            srcRoot = lib.RootFolder.ServerRelativeUrl;
            // Ensure the folder exists.
            lib.RootFolder.Folders.Add("OldFolder");
            await ctx.ExecuteQueryAsync();
        }

        // A recent file inside the folder (Modified = now, well after the cutoff).
        var buf = new byte[300];
        new Random(4).NextBytes(buf);
        await site.Rest.PostBinaryAsync(
            $"{webUrl}/_api/web/GetFolderByServerRelativeUrl('{srcRoot.Replace("'", "''")}/OldFolder')/Files/add(url='recent.bin',overwrite=true)",
            buf, buf.Length);

        // Back-date the FOLDER's Modified to before the cutoff so the old code drops it.
        using (var ctx = site.CreateContext())
        {
            var folderItem = ctx.Web.GetFolderByServerRelativeUrl($"{srcRoot}/OldFolder").ListItemAllFields;
            folderItem["Modified"] = oldDate;
            folderItem.UpdateOverwriteVersion();
            await ctx.ExecuteQueryAsync();
            ctx.Load(folderItem, i => i["Modified"]);
            await ctx.ExecuteQueryAsync();
            var got = folderItem["Modified"] is DateTime d ? d.ToUniversalTime() : DateTime.MaxValue;
            Program.Check("h4: folder back-dated below the cutoff (setup)", got < cutoff, $"folder Modified={got:yyyy-MM-dd}");
        }

        // ---- Copy with a modified-since filter set to the cutoff ----
        using (var ctx = site.CreateContext()) await TestAssets.DeleteIfExistsAsync(ctx, TgtTitle);
        var res = await CopyEngine.CopyListAsync(site, site, SrcTitle, new CopyOptions
        {
            TargetListTitle = TgtTitle,
            TargetListUrl = "MigTestH4Tgt",
            ModifiedSinceUtc = cutoff,
            DateField = DateFilterField.Modified,
        });

        var recent = res.Records.FirstOrDefault(r => r.ItemType == "File" && r.SourcePath.EndsWith("/recent.bin"));
        Console.WriteLine($"  recent.bin -> {recent?.Status} ({recent?.Message})");
        foreach (var r in res.Records.Where(r => r.Status == ItemCopyStatus.Failed))
            Console.WriteLine($"    [Failed] {r.ItemType}: {r.SourcePath} - {r.Message}");

        long tgtCount = -1;
        try
        {
            using var ctx = site.CreateContext();
            var l = ctx.Web.Lists.GetByTitle(TgtTitle);
            ctx.Load(l, x => x.ItemCount);
            await ctx.ExecuteQueryAsync();
            tgtCount = l.ItemCount;
        }
        catch { }

        // The recent file inside the old folder must copy (folder exempt from the date filter).
        Program.Check("h4: recent file inside an old folder is Copied (not Failed)",
            recent?.Status == ItemCopyStatus.Copied, $"{recent?.Status}: {recent?.Message}");
        Program.Check("h4: no failures from a missing parent folder", res.Failed == 0, res.Summary());
        Console.WriteLine($"  target item count: {tgtCount}");
    }
}

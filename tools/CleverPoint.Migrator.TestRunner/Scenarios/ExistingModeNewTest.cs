using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Reproduces review finding C3: with ExistingMode = CopyIfNewer (or Skip),
/// FileCopier.ReadServerModifiedAsync loads the target file's list item with no
/// try/catch. For a file that does not exist on the target yet, CSOM throws
/// "File Not Found", which is recorded as Failed, so the new file is never
/// copied. An identical Overwrite run copies it fine, isolating the mode.
///
/// Expected WHILE THE BUG IS PRESENT:
///   PASS  existing-mode-new: Overwrite copies a new file
///   FAIL  existing-mode-new: CopyIfNewer copies a new file  (recorded Failed)
/// The FAIL is the confirmation. After the fix, both PASS.
/// </summary>
public static class ExistingModeNewTest
{
    private const string SrcTitle = "MigTest-ExistingMode-Src";

    public static async Task RunAsync()
    {
        var site = Program.TestSite ?? await TestAssets.EnsureTestSiteAsync(Program.Source);
        Program.TestSite = site;

        // ---- One small source file (normal, non-chunked path) ----
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
                    Url = "MigTestExistingModeSrc",
                });
                await ctx.ExecuteQueryAsync();
            }
            var lib = ctx.Web.Lists.GetByTitle(SrcTitle);
            ctx.Load(lib.RootFolder, f => f.ServerRelativeUrl);
            await ctx.ExecuteQueryAsync();
            srcRoot = lib.RootFolder.ServerRelativeUrl;
        }
        var buf = new byte[1000];
        new Random(42).NextBytes(buf);
        await site.Rest.PostBinaryAsync(
            $"{webUrl}/_api/web/GetFolderByServerRelativeUrl('{srcRoot.Replace("'", "''")}')/Files/add(url='doc1.bin',overwrite=true)",
            buf, buf.Length);

        // ---- Copy into a FRESH target with CopyIfNewer (file is new there) ----
        var newerTgt = "MigTest-ExistingMode-Newer";
        using (var ctx = site.CreateContext()) await TestAssets.DeleteIfExistsAsync(ctx, newerTgt);
        var newerRes = await CopyEngine.CopyListAsync(site, site, SrcTitle, new CopyOptions
        {
            TargetListTitle = newerTgt,
            TargetListUrl = "MigTestExistingModeNewer",
            ExistingMode = ExistingItemMode.CopyIfNewer,
        });
        var newerFile = newerRes.Records.FirstOrDefault(r => r.ItemType == "File");
        Console.WriteLine($"  CopyIfNewer: {newerFile?.Status} ({newerFile?.Message})");

        // ---- Control: same copy with Overwrite ----
        var overTgt = "MigTest-ExistingMode-Over";
        using (var ctx = site.CreateContext()) await TestAssets.DeleteIfExistsAsync(ctx, overTgt);
        var overRes = await CopyEngine.CopyListAsync(site, site, SrcTitle, new CopyOptions
        {
            TargetListTitle = overTgt,
            TargetListUrl = "MigTestExistingModeOver",
            ExistingMode = ExistingItemMode.Overwrite,
        });
        var overFile = overRes.Records.FirstOrDefault(r => r.ItemType == "File");
        Console.WriteLine($"  Overwrite:   {overFile?.Status} ({overFile?.Message})");

        // Control proves the copy itself works.
        Program.Check("existing-mode-new: Overwrite copies a new file",
            overFile?.Status == ItemCopyStatus.Copied, overFile?.Message ?? "no record");
        // The finding: a brand-new file under CopyIfNewer is recorded Failed instead of Copied.
        Program.Check("existing-mode-new: CopyIfNewer copies a new file (FAIL = bug confirmed)",
            newerFile?.Status == ItemCopyStatus.Copied, $"{newerFile?.Status}: {newerFile?.Message}");
    }
}

using CleverPoint.Migrator.Core.Auth;
using CleverPoint.Migrator.Core.Csom;
using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Copying the CONTENTS OF A FOLDER must copy that folder only.
///
/// Reported: standing in Bench-A1/Folder-A/Sub-1 with nothing ticked and copying into
/// Target-Test/Subfolder landed the ENTIRE source library (every file from the list root down) in the
/// target folder. Cause: the Explorer never passed the open source folder, and an empty selection means
/// "whole list" to the engine -- so the source pane's folder was ignored while the target pane's was
/// honoured. This asserts the engine contract the fix relies on (SourceFolderServerRelativeUrl scopes the
/// copy), and includes the teeth: without it, the whole library really does land.
/// </summary>
public static class FolderScopeTest
{
    private const string SourceSite = "https://gocleverpointcom.sharepoint.com/sites/LMAS";
    private const string TargetSite = "https://gocleverpointcom.sharepoint.com/sites/DemoLargeSite";
    private const string SourceLib = "Bench-A1";
    private const string SourceFolderRel = "Folder-A/Sub-1";
    private const string TargetLib = "FolderScope-Target";
    private const string TargetSub = "Subfolder";

    public static async Task RunAsync()
    {
        var source = new SpConnection(SourceSite, new CertTokenProvider(Program.SourceCreds));
        var target = new SpConnection(TargetSite, new CertTokenProvider(Program.SourceCreds));

        // What is actually inside the source folder we are scoping to?
        string srcRoot;
        List<string> expected;
        using (var ctx = source.CreateContext())
        {
            var l = ctx.Web.Lists.GetByTitle(SourceLib);
            ctx.Load(l, x => x.RootFolder.ServerRelativeUrl, x => x.ItemCount);
            await ctx.ExecuteWithRetryAsync();
            srcRoot = l.RootFolder.ServerRelativeUrl;

            var folder = ctx.Web.GetFolderByServerRelativeUrl($"{srcRoot}/{SourceFolderRel}");
            var files = folder.Files;
            ctx.Load(files, fs => fs.Include(f => f.Name));
            await ctx.ExecuteWithRetryAsync();
            expected = files.AsEnumerable().Select(f => f.Name).OrderBy(n => n).ToList();
            Console.WriteLine($"  source library '{SourceLib}' holds {l.ItemCount} item(s) overall");
            Console.WriteLine($"  source folder '{SourceFolderRel}' holds {expected.Count} file(s): {string.Join(", ", expected)}");
        }
        Program.Check("folder-scope: source folder has files to copy", expected.Count > 0, $"{expected.Count}");

        var scopedUrl = $"{srcRoot}/{SourceFolderRel}";

        // ---- THE FIX: scope the copy to the open source folder ----
        var scoped = await CopyAsync(source, target, sourceFolder: scopedUrl, label: "scoped");
        Program.Check("folder-scope [scoped]: copy produced no failures", scoped.Failed == 0, $"{scoped.Failed} failure(s)");

        var landed = await TargetFilesAsync(target);
        Program.Check("folder-scope [scoped]: ONLY the folder's files landed",
            landed.OrderBy(n => n).SequenceEqual(expected),
            $"landed=[{string.Join(", ", landed.OrderBy(n => n))}]");

        // Nothing from the library ROOT may appear. This is the actual symptom that was reported.
        var rootLeakage = await RootFileNamesAsync(source, srcRoot);
        var leaked = landed.Intersect(rootLeakage, StringComparer.OrdinalIgnoreCase).ToList();
        Program.Check("folder-scope [scoped]: no files from the library ROOT leaked in",
            leaked.Count == 0, leaked.Count == 0 ? "none" : $"leaked: {string.Join(", ", leaked.Take(5))}");

        // ---- TEETH: no folder scope = the whole library lands (the reported bug) ----
        var whole = await CopyAsync(source, target, sourceFolder: null, label: "unscoped");
        var landedAll = await TargetFilesAsync(target);
        Program.Check("folder-scope [unscoped]: without the scope the WHOLE library lands (proves the bug)",
            landedAll.Count > expected.Count,
            $"{landedAll.Count} file(s) vs {expected.Count} in the folder");
    }

    private static async Task<CopyResult> CopyAsync(SpConnection source, SpConnection target, string? sourceFolder, string label)
    {
        using (var tctx = target.CreateContext())
            await TestAssets.DeleteIfExistsAsync(tctx, TargetLib);

        // Recreate the target library + the destination subfolder, mirroring "open a target folder".
        using (var tctx = target.CreateContext())
        {
            var lib = tctx.Web.Lists.Add(new ListCreationInformation
            {
                Title = TargetLib, TemplateType = (int)ListTemplateType.DocumentLibrary, Url = TargetLib,
            });
            tctx.Load(lib, l => l.RootFolder.ServerRelativeUrl);
            await tctx.ExecuteWithRetryAsync();
            tctx.Web.GetFolderByServerRelativeUrl(lib.RootFolder.ServerRelativeUrl)
                .Folders.Add($"{lib.RootFolder.ServerRelativeUrl}/{TargetSub}");
            await tctx.ExecuteWithRetryAsync();
        }

        var options = new CopyOptions
        {
            TargetListTitle = TargetLib,
            TargetListUrl = TargetLib,
            TargetSubfolderRelative = TargetSub,
            SourceFolderServerRelativeUrl = sourceFolder,
            CopyContent = true,
            MergeSchema = false,
            CopyViews = false,
            CopyListSettings = false,
        };
        var result = await CopyEngine.CopyListAsync(source, target, SourceLib, options);
        foreach (var f in result.Records.Where(r => r.Status == ItemCopyStatus.Failed).Take(3))
            Console.WriteLine($"    [FAILED] [{label}] {f.ItemType} {f.SourcePath}: {f.Message}");
        return result;
    }

    /// <summary>Every file that landed under the target subfolder.</summary>
    private static async Task<List<string>> TargetFilesAsync(SpConnection target)
    {
        using var ctx = target.CreateContext();
        var list = ctx.Web.Lists.GetByTitle(TargetLib);
        var items = list.GetItems(new CamlQuery
        {
            ViewXml = "<View Scope='RecursiveAll'><Query><Where><Eq><FieldRef Name='FSObjType'/>"
                      + "<Value Type='Integer'>0</Value></Eq></Where></Query>"
                      + "<ViewFields><FieldRef Name='FileLeafRef'/></ViewFields><RowLimit>500</RowLimit></View>",
        });
        ctx.Load(items, c => c.Include(i => i["FileLeafRef"]));
        await ctx.ExecuteWithRetryAsync();
        return items.AsEnumerable().Select(i => i["FileLeafRef"]?.ToString() ?? "").ToList();
    }

    /// <summary>File names sitting directly in the source library ROOT (must never land in a scoped copy).</summary>
    private static async Task<List<string>> RootFileNamesAsync(SpConnection source, string srcRoot)
    {
        using var ctx = source.CreateContext();
        var files = ctx.Web.GetFolderByServerRelativeUrl(srcRoot).Files;
        ctx.Load(files, fs => fs.Include(f => f.Name));
        await ctx.ExecuteWithRetryAsync();
        return files.AsEnumerable().Select(f => f.Name).ToList();
    }
}

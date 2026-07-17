using CleverPoint.Migrator.Core.Auth;
using CleverPoint.Migrator.Core.Csom;
using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Ticking a FOLDER while browsing inside a parent must land that folder directly in the target -- not
/// rebuild the ancestor chain above it.
///
/// Reported: browsing "Documents/Certificate Authority and Infra Discovery", ticking "Documentation", and
/// copying into "Client/Organic Valley" produced "Client/Organic Valley/Certificate Authority and Infra
/// Discovery/Documentation/..." -- it recreated the PARENT ("Certificate...") instead of just Documentation.
/// Cause: ticked paths were laid out relative to the LIST ROOT, ignoring the folder the user was in.
///
/// This provisions Parent/Child/(files) + Parent/loose.txt, browses "Parent", ticks "Child", copies into
/// a target subfolder, and asserts Child lands directly (Target/Sub/Child/...), with no "Parent" level and
/// no sibling leakage. Teeth: without the browse-folder base, "Parent/Child" is what lands.
/// </summary>
public static class TickedFolderScopeTest
{
    private const string Site = "https://gocleverpointcom.sharepoint.com/sites/DemoLargeSite";
    private const string SrcLib = "TickScope-Source";
    private const string TgtLib = "TickScope-Target";
    private const string TargetSub = "Dest/Inner";
    private const string Parent = "Parent";
    private const string Child = "Child";

    public static async Task RunAsync()
    {
        var conn = new SpConnection(Site, new CertTokenProvider(Program.SourceCreds));
        var (srcRoot, childFiles) = await ProvisionAsync(conn);
        var childPath = $"{srcRoot}/{Parent}/{Child}";

        // ---- THE FIX: browsing "Parent", ticking "Child", into "Dest/Inner" ----
        var fixedResult = await CopyAsync(conn,
            selected: new List<string> { childPath },
            sourceFolder: $"{srcRoot}/{Parent}",   // the folder open on the left
            label: "fixed");
        Program.Check("tick-scope [fixed]: copy produced no failures", fixedResult.Failed == 0, $"{fixedResult.Failed}");

        var landed = await TargetTreeAsync(conn);
        // Child must be directly under the target subfolder.
        var expectedDir = $"{TargetSub}/{Child}";
        Program.Check("tick-scope [fixed]: 'Child' landed directly under the target folder",
            landed.Any(p => p.Contains($"/{expectedDir}/", StringComparison.OrdinalIgnoreCase)),
            Sample(landed));
        // The PARENT level must NOT have been recreated.
        Program.Check("tick-scope [fixed]: the ancestor 'Parent' was NOT recreated",
            !landed.Any(p => p.Contains($"/{TargetSub}/{Parent}/", StringComparison.OrdinalIgnoreCase)),
            Sample(landed));
        // All the child's files are present.
        var landedLeaves = landed.Where(p => p.Contains($"/{Child}/", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Split('/')[^1]).Where(n => n.EndsWith(".txt")).OrderBy(n => n).ToList();
        Program.Check("tick-scope [fixed]: every file inside 'Child' came across",
            landedLeaves.SequenceEqual(childFiles.OrderBy(n => n)), string.Join(", ", landedLeaves));
        // The sibling loose file at Parent level must not appear (only Child was ticked).
        Program.Check("tick-scope [fixed]: the sibling 'loose.txt' at Parent level did NOT leak in",
            !landed.Any(p => p.EndsWith("/loose.txt", StringComparison.OrdinalIgnoreCase)), Sample(landed));

        // ---- TEETH: no browse-folder base = the parent chain is rebuilt (the reported bug) ----
        var buggy = await CopyAsync(conn, selected: new List<string> { childPath }, sourceFolder: null, label: "buggy");
        var landedBuggy = await TargetTreeAsync(conn);
        Program.Check("tick-scope [buggy]: without the base, the 'Parent' ancestor IS recreated (proves the bug)",
            landedBuggy.Any(p => p.Contains($"/{TargetSub}/{Parent}/{Child}/", StringComparison.OrdinalIgnoreCase)),
            Sample(landedBuggy));
    }

    private static async Task<CopyResult> CopyAsync(SpConnection conn, List<string> selected, string? sourceFolder, string label)
    {
        using (var ctx = conn.CreateContext())
            await TestAssets.DeleteIfExistsAsync(ctx, TgtLib);
        using (var ctx = conn.CreateContext())
        {
            var lib = ctx.Web.Lists.Add(new ListCreationInformation
            {
                Title = TgtLib, TemplateType = (int)ListTemplateType.DocumentLibrary, Url = TgtLib,
            });
            ctx.Load(lib, l => l.RootFolder.ServerRelativeUrl);
            await ctx.ExecuteWithRetryAsync();
            // Pre-create the target destination folder chain (mirrors "open a target folder").
            var cur = lib.RootFolder.ServerRelativeUrl;
            foreach (var seg in TargetSub.Split('/'))
            {
                ctx.Web.GetFolderByServerRelativeUrl(cur).Folders.Add($"{cur}/{seg}");
                await ctx.ExecuteWithRetryAsync();
                cur = $"{cur}/{seg}";
            }
        }

        var options = new CopyOptions
        {
            TargetListTitle = TgtLib,
            TargetListUrl = TgtLib,
            TargetSubfolderRelative = TargetSub,
            SelectedPaths = selected,
            SourceFolderServerRelativeUrl = sourceFolder,
            CopyContent = true,
            MergeSchema = false,
            CopyViews = false,
            CopyListSettings = false,
        };
        var result = await CopyEngine.CopyListAsync(conn, conn, SrcLib, options);
        foreach (var f in result.Records.Where(r => r.Status == ItemCopyStatus.Failed).Take(3))
            Console.WriteLine($"    [FAILED] [{label}] {f.ItemType} {f.SourcePath}: {f.Message}");
        return result;
    }

    private static async Task<(string Root, List<string> ChildFiles)> ProvisionAsync(SpConnection conn)
    {
        using var ctx = conn.CreateContext();
        await TestAssets.DeleteIfExistsAsync(ctx, SrcLib);
        var lib = ctx.Web.Lists.Add(new ListCreationInformation
        {
            Title = SrcLib, TemplateType = (int)ListTemplateType.DocumentLibrary, Url = SrcLib,
        });
        ctx.Load(lib, l => l.RootFolder.ServerRelativeUrl);
        await ctx.ExecuteWithRetryAsync();
        var root = lib.RootFolder.ServerRelativeUrl;

        foreach (var rel in new[] { Parent, $"{Parent}/{Child}", $"{Parent}/{Child}/Grand" })
        {
            var parent = rel.Contains('/') ? $"{root}/{rel[..rel.LastIndexOf('/')]}" : root;
            ctx.Web.GetFolderByServerRelativeUrl(parent).Folders.Add($"{root}/{rel}");
            await ctx.ExecuteWithRetryAsync();
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes("tick scope");
        var childFiles = new List<string> { "c1.txt", "c2.txt", "g1.txt" };
        async Task Add(string folderRel, string name)
        {
            ctx.Web.GetFolderByServerRelativeUrl($"{root}/{folderRel}")
                .Files.Add(new FileCreationInformation { Url = name, Content = bytes, Overwrite = true });
            await ctx.ExecuteWithRetryAsync();
        }
        await Add($"{Parent}/{Child}", "c1.txt");
        await Add($"{Parent}/{Child}", "c2.txt");
        await Add($"{Parent}/{Child}/Grand", "g1.txt");
        await Add(Parent, "loose.txt");   // sibling of Child; must NOT come across when only Child is ticked

        Console.WriteLine($"  provisioned '{SrcLib}': {Parent}/{Child} (2 files + Grand/g1.txt) and {Parent}/loose.txt");
        return (root, childFiles);
    }

    private static async Task<List<string>> TargetTreeAsync(SpConnection conn)
    {
        using var ctx = conn.CreateContext();
        var list = ctx.Web.Lists.GetByTitle(TgtLib);
        var items = list.GetItems(new CamlQuery
        {
            ViewXml = "<View Scope='RecursiveAll'><ViewFields><FieldRef Name='FileRef'/></ViewFields>"
                      + "<RowLimit>500</RowLimit></View>",
        });
        ctx.Load(items, c => c.Include(i => i["FileRef"]));
        await ctx.ExecuteWithRetryAsync();
        return items.AsEnumerable().Select(i => i["FileRef"]?.ToString() ?? "").ToList();
    }

    private static string Sample(List<string> paths) =>
        paths.Count == 0 ? "<empty>" : string.Join(" | ", paths.OrderBy(p => p).Take(6).Select(p => p.Split("/Documents/").LastOrDefault() ?? p));
}

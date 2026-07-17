using System.Diagnostics;
using CleverPoint.Migrator.Core.Csom;
using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// List View Threshold (LVT) stress rig. A user hit "The attempted operation is prohibited because it
/// exceeds the list view threshold" moving folders between document libraries. LVT (5,000 rows) bites a
/// query that filters/sorts on a non-indexed column, or returns more than 5,000 rows without paging --
/// per LIST, and also per FOLDER when a single folder holds more than 5,000 items.
///
/// Scenarios:
///   lvt-inspect  -- report the libraries on DemoLargeSite: item counts, folder counts, biggest folder.
///   lvt-provision-- build a source folder tree with &gt;10,000 tiny files, including a single sub-subfolder
///                   holding &gt;5,000 (the folder-level threshold), and a target library also over 10,000.
///                   Resumable: re-running tops up rather than restarting.
///   lvt-copy     -- copy that folder tree into the big target and surface exactly which call throws.
/// </summary>
public static class LvtStressTest
{
    private const string Site = "https://gocleverpointcom.sharepoint.com/sites/DemoLargeSite";

    // Source tree: one folder over the FOLDER-level threshold, plus siblings to clear 10k overall.
    public const string SrcLib = "LVT-Source";
    public const string TgtLib = "LVT-Target";
    public const string RootFolder = "Stress";
    private const int BatchSize = 100;

    private static SpConnection Conn() => new(Site, new Core.Auth.CertTokenProvider(Program.SourceCreds));

    // ---------------------------------------------------------------- inspect

    public static async Task InspectAsync()
    {
        using var ctx = Conn().CreateContext();
        var lists = ctx.Web.Lists;
        ctx.Load(lists, ls => ls.Include(l => l.Title, l => l.ItemCount, l => l.BaseType, l => l.Hidden,
            l => l.RootFolder.ServerRelativeUrl));
        await ctx.ExecuteQueryAsync();

        Console.WriteLine($"  Libraries on {Site}:");
        foreach (var l in lists.AsEnumerable()
                     .Where(l => !l.Hidden && l.BaseType == BaseType.DocumentLibrary)
                     .OrderByDescending(l => l.ItemCount))
            Console.WriteLine($"      {l.ItemCount,8:N0} items   {l.Title}");

        // Enumerating Folder.Folders is itself LVT-bound: on a library whose root holds >5,000 children it
        // throws rather than returning the (few) subfolders. Report that instead of crashing -- it is a
        // real hazard for any code that walks folders via the object model / REST rather than paged CAML.
        foreach (var title in new[] { "Library 3", "Library 4" })
        {
            var l = ctx.Web.Lists.GetByTitle(title);
            ctx.Load(l, x => x.RootFolder.ServerRelativeUrl, x => x.RootFolder.ItemCount);
            await ctx.ExecuteQueryAsync();
            Console.WriteLine($"  {title}: root folder holds {l.RootFolder.ItemCount:N0} direct child(ren)");

            try
            {
                var sub = l.RootFolder.Folders;
                ctx.Load(sub, fs => fs.Include(f => f.Name, f => f.ItemCount));
                await ctx.ExecuteQueryAsync();
                foreach (var f in sub.AsEnumerable().OrderByDescending(f => f.ItemCount).Take(8))
                    Console.WriteLine($"      {f.ItemCount,8:N0}  {f.Name}/");
            }
            catch (ServerException ex) when (IsLvt(ex))
            {
                Console.WriteLine($"      *** Folder.Folders enumeration THREW LVT on '{title}' ***");
            }
        }

        Program.Check("lvt-inspect: read the site's libraries", true, $"{lists.Count} list(s)");
    }

    /// <summary>The list-view-threshold error, whatever wraps it.</summary>
    public static bool IsLvt(Exception ex) =>
        ex.Message.Contains("list view threshold", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("exceeds the list view", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------- provision

    /// <summary>
    /// Source tree (all counts are files, each ~20 bytes):
    ///   Stress/Big            6,000   &lt;- single folder ABOVE the 5,000 folder threshold
    ///   Stress/Nest/L2/L3     5,200   &lt;- deep sub-subfolder, also above
    ///   Stress/Small-000..019   100   &lt;- 20 folders x 5, breadth
    /// Total under Stress: ~11,300 -> the library is also far above the LIST threshold.
    /// </summary>
    private static readonly (string Rel, int Count)[] SourcePlan =
    {
        ($"{RootFolder}/Big", 6000),
        ($"{RootFolder}/Nest/L2/L3", 5200),
    };

    public static async Task ProvisionAsync()
    {
        var conn = Conn();
        var sw = Stopwatch.StartNew();

        // --- source ---
        var srcRoot = await EnsureLibraryAsync(conn, SrcLib);
        var plan = SourcePlan.ToList();
        for (var i = 0; i < 20; i++) plan.Add(($"{RootFolder}/Small-{i:D3}", 5));
        foreach (var (rel, count) in plan)
        {
            await EnsureFolderChainAsync(conn, srcRoot, rel);
            await FillFolderAsync(conn, $"{srcRoot}/{rel}", count, sw);
        }

        // --- target: also over 10,000, nested in sub-subfolders, so target-side queries face the same
        //     list-level AND folder-level thresholds the source does ---
        var tgtRoot = await EnsureLibraryAsync(conn, TgtLib);
        foreach (var (rel, count) in new[] { ("Existing/E2/E3", 5600), ("Existing/E2/E4", 5200) })
        {
            await EnsureFolderChainAsync(conn, tgtRoot, rel);
            await FillFolderAsync(conn, $"{tgtRoot}/{rel}", count, sw);
        }

        await ReportAsync(conn, sw);
    }

    private static async Task ReportAsync(SpConnection conn, Stopwatch sw)
    {
        using var ctx = conn.CreateContext();
        foreach (var title in new[] { SrcLib, TgtLib })
        {
            var l = ctx.Web.Lists.GetByTitle(title);
            ctx.Load(l, x => x.ItemCount);
            await ctx.ExecuteQueryAsync();
            Console.WriteLine($"  {title}: {l.ItemCount:N0} items");
            Program.Check($"lvt-provision: {title} is above the 5,000 list threshold",
                l.ItemCount > 5000, $"{l.ItemCount:N0} items");
        }
        Console.WriteLine($"  provisioning time: {sw.Elapsed.TotalMinutes:F1} min");
    }

    private static async Task<string> EnsureLibraryAsync(SpConnection conn, string title)
    {
        using var ctx = conn.CreateContext();
        var lists = ctx.Web.Lists;
        ctx.Load(lists, ls => ls.Include(l => l.Title));
        await ctx.ExecuteQueryAsync();

        List list;
        if (lists.AsEnumerable().Any(l => l.Title == title))
        {
            list = ctx.Web.Lists.GetByTitle(title);
        }
        else
        {
            list = ctx.Web.Lists.Add(new ListCreationInformation
            {
                Title = title, TemplateType = (int)ListTemplateType.DocumentLibrary,
                Url = title.Replace("-", ""),
            });
            await ctx.ExecuteQueryAsync();
            Console.WriteLine($"  created library '{title}'");
        }
        ctx.Load(list.RootFolder, f => f.ServerRelativeUrl);
        await ctx.ExecuteQueryAsync();
        return list.RootFolder.ServerRelativeUrl;
    }

    private static async Task EnsureFolderChainAsync(SpConnection conn, string root, string rel)
    {
        using var ctx = conn.CreateContext();
        var current = root;
        foreach (var seg in rel.Split('/'))
        {
            var next = $"{current}/{seg}";
            try
            {
                ctx.Web.GetFolderByServerRelativeUrl(current).Folders.Add(next);
                await ctx.ExecuteQueryAsync();
            }
            catch (ServerException) { /* already there */ }
            current = next;
        }
    }

    /// <summary>Tops a folder up to <paramref name="target"/> files. Resumable: counts what's there first.</summary>
    private static async Task FillFolderAsync(SpConnection conn, string folderUrl, int target, Stopwatch sw)
    {
        using var ctx = conn.CreateContext();
        var folder = ctx.Web.GetFolderByServerRelativeUrl(folderUrl);
        ctx.Load(folder, f => f.ItemCount);
        await ctx.ExecuteQueryAsync();
        var have = folder.ItemCount;
        if (have >= target)
        {
            Console.WriteLine($"  {folderUrl.Split('/')[^1]}: {have:N0} already there (skip)");
            return;
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes("lvt stress test file");
        var added = 0;
        for (var i = have; i < target; i++)
        {
            ctx.Web.GetFolderByServerRelativeUrl(folderUrl).Files.Add(new FileCreationInformation
            {
                Url = $"f{i:D5}.txt", Content = bytes, Overwrite = true,
            });
            added++;
            if (added % BatchSize == 0)
            {
                await ctx.ExecuteQueryAsync();
                Console.WriteLine($"  {folderUrl.Split('/')[^1]}: {i + 1:N0}/{target:N0} ({sw.Elapsed.TotalMinutes:F1} min)");
            }
        }
        await ctx.ExecuteQueryAsync();
        Console.WriteLine($"  {folderUrl.Split('/')[^1]}: filled to {target:N0} ({sw.Elapsed.TotalMinutes:F1} min)");
    }

    // ------------------------------------------------------------------ copy

    /// <summary>Copies the big source folder tree into the big target library and reports any LVT failure.</summary>
    public static async Task CopyAsync()
    {
        var conn = Conn();
        string srcRoot;
        using (var ctx = conn.CreateContext())
        {
            var l = ctx.Web.Lists.GetByTitle(SrcLib);
            ctx.Load(l, x => x.RootFolder.ServerRelativeUrl, x => x.ItemCount);
            await ctx.ExecuteQueryAsync();
            srcRoot = l.RootFolder.ServerRelativeUrl;
            Console.WriteLine($"  source '{SrcLib}': {l.ItemCount:N0} items");
        }

        // Exactly what the user does: tick the folder in the Explorer and copy it -> SelectedPaths.
        var options = new CopyOptions
        {
            TargetListTitle = TgtLib,
            TargetListUrl = TgtLib.Replace("-", ""),
            CopyContent = true,
            CopyListSettings = false,
            CopyViews = false,
            SelectedPaths = new List<string> { $"{srcRoot}/{RootFolder}" },
        };

        var sw = Stopwatch.StartNew();
        CopyResult result;
        try
        {
            result = await CopyEngine.CopyListAsync(conn, conn, SrcLib, options);
        }
        catch (Exception ex)
        {
            Program.Check("lvt-copy: run completed without throwing", false, Describe(ex));
            return;
        }

        var lvt = result.Records
            .Where(r => r.Status == ItemCopyStatus.Failed
                        && (r.Message ?? "").Contains("list view threshold", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var r in lvt.Take(5))
            Console.WriteLine($"    [LVT] {r.ItemType} {r.SourcePath}: {r.Message}");

        var failed = result.Records.Where(r => r.Status == ItemCopyStatus.Failed).ToList();
        foreach (var f in failed.Take(5))
            Console.WriteLine($"    [FAILED] {f.ItemType} {f.SourcePath}: {f.Message}");

        Program.Check("lvt-copy: no list-view-threshold failures", lvt.Count == 0, $"{lvt.Count} LVT failure(s)");
        Program.Check("lvt-copy: no failures at all", failed.Count == 0, $"{failed.Count} failure(s)");
        Console.WriteLine($"  copied {result.Copied:N0} in {sw.Elapsed.TotalMinutes:F1} min");
    }

    /// <summary>
    /// Focused, fast check of the ENGINE's folder-scoped scan against the >5,000-item folder, without
    /// running a full copy. Proves LoadAllItemsAsync returns every item in the big folder (via its
    /// whole-list fallback) rather than throwing the list view threshold.
    /// </summary>
    public static async Task ScanAsync()
    {
        var conn = Conn();
        string srcRoot;
        using (var ctx = conn.CreateContext())
        {
            var l = ctx.Web.Lists.GetByTitle(SrcLib);
            ctx.Load(l, x => x.RootFolder.ServerRelativeUrl);
            await ctx.ExecuteQueryAsync();
            srcRoot = l.RootFolder.ServerRelativeUrl;
        }

        // Expected count straight from the folder object (cheap, not LVT-bound).
        int expected;
        using (var ctx = conn.CreateContext())
        {
            var f = ctx.Web.GetFolderByServerRelativeUrl($"{srcRoot}/{RootFolder}/Big");
            ctx.Load(f, x => x.ItemCount);
            await ctx.ExecuteQueryAsync();
            expected = f.ItemCount;
        }
        Console.WriteLine($"  'Big' folder holds {expected:N0} items");

        using var sctx = conn.CreateContext();
        var list = sctx.Web.Lists.GetByTitle(SrcLib);
        var users = new UserResolver(sctx, sctx, null, null);
        await users.PrimeSourceUsersAsync();
        var copier = new ItemCopier(sctx, sctx, users);

        var options = new CopyOptions { SourceFolderServerRelativeUrl = $"{srcRoot}/{RootFolder}/Big" };
        List<ListItem> items;
        try
        {
            items = await copier.LoadAllItemsAsync(list, options);
        }
        catch (Exception ex)
        {
            Program.Check("lvt-scan: folder scan did not throw the list view threshold", false, Describe(ex));
            return;
        }

        var files = items.Count(i => i.FileSystemObjectType == FileSystemObjectType.File);
        Program.Check("lvt-scan: >5,000-item folder scanned without LVT", true, $"{items.Count:N0} item(s)");
        Program.Check("lvt-scan: scan returned every file in the folder", files == expected, $"{files:N0}/{expected:N0}");
        Program.Check("lvt-scan: every item really is under the scoped folder",
            items.All(i => ((string)i["FileRef"]).Contains($"/{RootFolder}/Big/", StringComparison.OrdinalIgnoreCase)),
            "path-scoped");
    }

    private static string Describe(Exception ex) =>
        ex.Message.Contains("list view threshold", StringComparison.OrdinalIgnoreCase)
            ? $"*** LIST VIEW THRESHOLD *** {ex.Message}\n{ex.StackTrace}"
            : $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
}

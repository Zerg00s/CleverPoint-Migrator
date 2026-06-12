using System.Diagnostics;
using CleverPoint.Migrator.Core.MigrationApi;
using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using CleverPoint.Migrator.Core.Validation;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Scale tests: a multi-chunk Migration API run over a 400-file library with
/// a 4-level folder tree, and a 1,200-item list through the classic engine.
/// Watches process memory to prove the streaming design stays flat.
/// </summary>
public static class ScaleTests
{
    public const string BigLibTitle = "MigTest-BigLib";
    public const string BigListTitle = "MigTest-BigList";
    private const int FileCount = 400;
    private const int ItemCount = 1200;

    public static async Task ProvisionBigLibraryAsync()
    {
        var site = Program.TestSite ?? await TestAssets.EnsureTestSiteAsync(Program.Source);
        Program.TestSite = site;
        using var ctx = site.CreateContext();

        // Reuse if already provisioned with the right count (idempotent, slow to build).
        var lists = ctx.Web.Lists;
        ctx.Load(lists, ls => ls.Include(l => l.Title, l => l.ItemCount));
        await ctx.ExecuteQueryAsync();
        var existing = lists.AsEnumerable().FirstOrDefault(l => l.Title == BigLibTitle);
        if (existing != null && existing.ItemCount >= FileCount)
        {
            Program.Check("scale: big library present", true, $"{existing.ItemCount} items (reused)");
            return;
        }

        List list;
        if (existing == null)
        {
            list = ctx.Web.Lists.Add(new ListCreationInformation
            {
                Title = BigLibTitle,
                TemplateType = (int)ListTemplateType.DocumentLibrary,
                Url = "MigTestBigLib",
            });
            await ctx.ExecuteQueryAsync();
        }
        else
        {
            list = ctx.Web.Lists.GetByTitle(BigLibTitle);
        }
        ctx.Load(list.RootFolder, f => f.ServerRelativeUrl);
        await ctx.ExecuteQueryAsync();
        var root = list.RootFolder.ServerRelativeUrl;

        // 4-level folder tree: D1-x / D2-y / D3-z / D4-w.
        var rng = new Random(777);
        var dirs = new List<string>();
        for (var a = 1; a <= 6; a++)
        {
            dirs.Add($"D1-{a}");
            for (var b = 1; b <= 2; b++)
            {
                dirs.Add($"D1-{a}/D2-{b}");
                if (a % 2 == 0) dirs.Add($"D1-{a}/D2-{b}/D3-1");
                if (a == 4 && b == 1) dirs.Add($"D1-{a}/D2-{b}/D3-1/D4-1");
            }
        }
        foreach (var dir in dirs)
        {
            var parent = dir.Contains('/') ? $"{root}/{dir[..dir.LastIndexOf('/')]}" : root;
            try
            {
                ctx.Web.GetFolderByServerRelativeUrl(parent).Folders.Add($"{root}/{dir}");
                await ctx.ExecuteQueryAsync();
            }
            catch (ServerException) { /* exists on re-run */ }
        }

        var locations = new[] { "" }.Concat(dirs).ToArray();
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < FileCount; i++)
        {
            var size = i % 25 == 0 ? 300_000 + rng.Next(1_500_000) : 1024 + rng.Next(7168);
            var bytes = new byte[size];
            rng.NextBytes(bytes);
            var location = locations[i % locations.Length];
            var folderUrl = string.IsNullOrEmpty(location) ? root : $"{root}/{location}";
            ctx.Web.GetFolderByServerRelativeUrl(folderUrl).Files.Add(new FileCreationInformation
            {
                Url = $"f{i:D4}.bin",
                ContentStream = new MemoryStream(bytes),
                Overwrite = true,
            });
            if (i % 20 == 19)
            {
                await ctx.ExecuteQueryAsync();
                if (i % 100 == 99) Console.WriteLine($"  provisioned {i + 1}/{FileCount} files ({sw.Elapsed.TotalSeconds:F0}s)");
            }
        }
        await ctx.ExecuteQueryAsync();
        Program.Check("scale: big library provisioned", true, $"{FileCount} files, {dirs.Count} folders in {sw.Elapsed.TotalMinutes:F1} min");
    }

    public static async Task ScaleApiCopyAsync()
    {
        var site = Program.TestSite ?? await TestAssets.EnsureTestSiteAsync(Program.Source);
        Program.TestSite = site;
        using (var ctx = site.CreateContext())
        {
            await TestAssets.DeleteIfExistsAsync(ctx, "MigTest-BigApiCopy");
        }

        var engine = new MigrationApiEngine(site, site);
        engine.OnProgress += msg => Console.WriteLine($"  [api] {msg}");
        var options = new CopyOptions
        {
            TargetListTitle = "MigTest-BigApiCopy",
            TargetListUrl = "MigTestBigApiCopy",
            ApiMaxItemsPerPackage = 120,   // forces ~4 chunks for 400 files + folders
        };

        var memBefore = Process.GetCurrentProcess().WorkingSet64;
        var sw = Stopwatch.StartNew();
        var result = await engine.CopyLibraryAsync(BigLibTitle, options);
        sw.Stop();
        var memAfter = Process.GetCurrentProcess().WorkingSet64;

        Console.WriteLine($"  duration: {sw.Elapsed.TotalMinutes:F1} min; working set {memBefore / 1048576} -> {memAfter / 1048576} MB");
        foreach (var r in result.Records.Where(r => r.Status == ItemCopyStatus.Failed).Take(8))
            Console.WriteLine($"    [Failed] {r.ItemType}: {r.Message}");
        Program.Check("scale api: no failures", result.Failed == 0, result.Summary());
        Program.Check("scale api: memory stayed flat", memAfter - memBefore < 600 * 1048576,
            $"grew {(memAfter - memBefore) / 1048576} MB");

        using var sourceCtx = site.CreateContext();
        using var targetCtx = site.CreateContext();
        var sourceList = sourceCtx.Web.Lists.GetByTitle(BigLibTitle);
        var targetList = targetCtx.Web.Lists.GetByTitle("MigTest-BigApiCopy");
        var verifier = new CopyVerifier(sourceCtx, targetCtx);
        var mismatches = await verifier.VerifyAsync(sourceList, targetList, Array.Empty<string>(),
            compareFileContent: true, contentSampleEvery: 10);   // every 10th file fully hashed
        foreach (var m in mismatches.Take(15)) Console.WriteLine($"    MISMATCH: {m}");
        Program.Check("scale api: verification clean (all metadata + sampled SHA-256)",
            mismatches.Count == 0, $"{mismatches.Count} mismatches");
    }

    public static async Task ScaleListCopyAsync()
    {
        var site = Program.TestSite ?? await TestAssets.EnsureTestSiteAsync(Program.Source);
        Program.TestSite = site;
        using var ctx = site.CreateContext();

        // Provision (or reuse) the big list.
        var lists = ctx.Web.Lists;
        ctx.Load(lists, ls => ls.Include(l => l.Title, l => l.ItemCount));
        await ctx.ExecuteQueryAsync();
        var existing = lists.AsEnumerable().FirstOrDefault(l => l.Title == BigListTitle);
        List list;
        if (existing == null || existing.ItemCount < ItemCount)
        {
            if (existing == null)
            {
                list = ctx.Web.Lists.Add(new ListCreationInformation
                {
                    Title = BigListTitle,
                    TemplateType = (int)ListTemplateType.GenericList,
                    Url = "Lists/MigTestBigList",
                });
                list.Fields.AddFieldAsXml("<Field Type='Text' Name='Payload' DisplayName='Payload' MaxLength='255' />",
                    true, AddFieldOptions.AddFieldInternalNameHint);
                await ctx.ExecuteQueryAsync();
            }
            else
            {
                list = ctx.Web.Lists.GetByTitle(BigListTitle);
            }

            var have = existing?.ItemCount ?? 0;
            var rng = new Random(555);
            var sw0 = Stopwatch.StartNew();
            for (var i = have; i < ItemCount; i++)
            {
                var item = list.AddItem(new ListItemCreationInformation());
                item["Title"] = $"Bulk {i + 1:D5}";
                item["Payload"] = $"payload-{rng.Next(100000)}";
                item.Update();
                if (i % 100 == 99)
                {
                    await ctx.ExecuteQueryAsync();
                    if (i % 400 == 399) Console.WriteLine($"  provisioned {i + 1}/{ItemCount} items ({sw0.Elapsed.TotalSeconds:F0}s)");
                }
            }
            await ctx.ExecuteQueryAsync();
            Console.WriteLine($"  big list ready ({sw0.Elapsed.TotalMinutes:F1} min)");
        }

        await TestAssets.DeleteIfExistsAsync(ctx, "MigTest-BigListCopy");

        var sw = Stopwatch.StartNew();
        var result = await CopyEngine.CopyListAsync(site, site, BigListTitle,
            new CopyOptions { TargetListTitle = "MigTest-BigListCopy", TargetListUrl = "Lists/MigTestBigListCopy", BatchSize = 50 });
        sw.Stop();

        var copied = result.Records.Count(r => r.ItemType == "Item" && r.Status == ItemCopyStatus.Copied);
        Console.WriteLine($"  duration: {sw.Elapsed.TotalMinutes:F1} min ({copied / Math.Max(1, sw.Elapsed.TotalSeconds):F1} items/s)");
        Program.Check("scale list: no failures", result.Failed == 0, result.Summary());
        Program.Check("scale list: all items copied", copied == ItemCount, $"{copied}/{ItemCount}");

        using var sourceCtx = site.CreateContext();
        using var targetCtx = site.CreateContext();
        var verifier = new CopyVerifier(sourceCtx, targetCtx);
        var mismatches = await verifier.VerifyAsync(
            sourceCtx.Web.Lists.GetByTitle(BigListTitle),
            targetCtx.Web.Lists.GetByTitle("MigTest-BigListCopy"),
            new[] { "Title", "Payload" });
        foreach (var m in mismatches.Take(10)) Console.WriteLine($"    MISMATCH: {m}");
        Program.Check("scale list: verification clean", mismatches.Count == 0, $"{mismatches.Count} mismatches");
    }
}

using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using CleverPoint.Migrator.Core.Validation;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// SharePoint-legal "interesting" characters end to end: file and folder
/// names (apostrophes, ampersands, unicode incl. Cyrillic and CJK, hash,
/// percent, plus, parentheses), list title with specials, column display
/// name, view title. Copied with BOTH engines and verified faithfully.
/// </summary>
public static class SpecialCharTests
{
    private const string LibTitle = "MigTest-Chars & Co's Lib";

    private static readonly string[] Folders =
    {
        "Q1 & Q2 (jan-mar)",
        "données d'été",
        "Отчёты 2024",
    };

    private static readonly string[] FileNames =
    {
        "O'Brien & Sons (2024) + plan 100%.bin",
        "café résumé.bin",
        "Привет мир.bin",
        "报告 #final.bin",
        "plain-name.bin",
        "Q1 & Q2 (jan-mar)/nested O'Hara's notes.bin",
        "données d'été/été #2 100%.bin",
        "Отчёты 2024/отчёт + итог.bin",
    };

    public static async Task RunAsync()
    {
        var site = Program.TestSite ?? await TestAssets.EnsureTestSiteAsync(Program.Source);
        Program.TestSite = site;

        // ---- Provision the special-character library ----
        using (var ctx = site.CreateContext())
        {
            var deleted = await TestAssets.DeleteIfExistsAsync(ctx, LibTitle);
            List lib;
            if (deleted)
            {
                lib = ctx.Web.Lists.Add(new ListCreationInformation
                {
                    Title = LibTitle,
                    TemplateType = (int)ListTemplateType.DocumentLibrary,
                    Url = "MigTestCharsLib",   // URL leaf deliberately differs from the title
                });
                lib.Fields.AddFieldAsXml(
                    "<Field Type='Text' Name='CharCol' DisplayName='Catégorie &amp; Über-col' MaxLength='120' />",
                    true, AddFieldOptions.AddFieldInternalNameHint);
                await ctx.ExecuteQueryAsync();
                lib.Views.Add(new ViewCreationInformation
                {
                    Title = "Vue d'été & co",
                    RowLimit = 30,
                    ViewFields = new[] { "DocIcon", "LinkFilename", "CharCol" },
                });
                await ctx.ExecuteQueryAsync();
            }
            else
            {
                lib = ctx.Web.Lists.GetByTitle(LibTitle);
            }
            ctx.Load(lib.RootFolder, f => f.ServerRelativeUrl);
            await ctx.ExecuteQueryAsync();
            var root = lib.RootFolder.ServerRelativeUrl;

            foreach (var folder in Folders)
            {
                try
                {
                    ctx.Web.GetFolderByServerRelativeUrl(root).Folders.Add($"{root}/{folder}");
                    await ctx.ExecuteQueryAsync();
                }
                catch (ServerException) { /* exists on re-run */ }
            }

            var rng = new Random(31337);
            foreach (var name in FileNames)
            {
                var bytes = new byte[1024 + rng.Next(4096)];
                rng.NextBytes(bytes);
                var dir = name.Contains('/') ? $"{root}/{name[..name.LastIndexOf('/')]}" : root;
                var leaf = name.Contains('/') ? name[(name.LastIndexOf('/') + 1)..] : name;
                var file = ctx.Web.GetFolderByServerRelativeUrl(dir).Files.Add(new FileCreationInformation
                {
                    Url = leaf,
                    ContentStream = new MemoryStream(bytes),
                    Overwrite = true,
                });
                ctx.Load(file, f => f.ListItemAllFields.Id);
                await ctx.ExecuteQueryAsync();
                var item = file.ListItemAllFields;
                item["CharCol"] = $"valeur d'été & {leaf[..Math.Min(20, leaf.Length)]}";
                item.UpdateOverwriteVersion();
                await ctx.ExecuteQueryAsync();
            }
            Console.WriteLine($"  provisioned '{LibTitle}': {FileNames.Length} files, {Folders.Length} folders");
        }

        // ---- Engine 1: classic copy ----
        using (var ctx = site.CreateContext())
        {
            await TestAssets.DeleteIfExistsAsync(ctx, "MigTest-CharsCopy");
        }
        var classic = await CopyEngine.CopyListAsync(site, site, LibTitle,
            new CopyOptions { TargetListTitle = "MigTest-CharsCopy", TargetListUrl = "MigTestCharsCopy" });
        foreach (var r in classic.Records.Where(r => r.Status == ItemCopyStatus.Failed).Take(8))
            Console.WriteLine($"    [Failed] {r.ItemType} {r.SourcePath}: {r.Message}");
        Program.Check("chars classic: no failures", classic.Failed == 0, classic.Summary());
        await VerifyAsync(site, "MigTest-CharsCopy", "classic");

        // ---- Engine 2: Migration API ----
        using (var ctx = site.CreateContext())
        {
            await TestAssets.DeleteIfExistsAsync(ctx, "MigTest-CharsApi");
        }
        var engine = new Core.MigrationApi.MigrationApiEngine(site, site);
        var api = await engine.CopyLibraryAsync(LibTitle,
            new CopyOptions { TargetListTitle = "MigTest-CharsApi", TargetListUrl = "MigTestCharsApi" });
        foreach (var r in api.Records.Where(r => r.Status == ItemCopyStatus.Failed).Take(8))
            Console.WriteLine($"    [Failed] {r.ItemType} {r.SourcePath}: {r.Message}");
        Program.Check("chars api: no failures", api.Failed == 0, api.Summary());
        await VerifyAsync(site, "MigTest-CharsApi", "api");

        // ---- Schema specials survived ----
        using (var vctx = site.CreateContext())
        {
            var tgt = vctx.Web.Lists.GetByTitle("MigTest-CharsCopy");
            var col = tgt.Fields.GetByInternalNameOrTitle("CharCol");
            vctx.Load(col, f => f.Title);
            vctx.Load(tgt.Views, vs => vs.Include(v => v.Title));
            await vctx.ExecuteQueryAsync();
            Program.Check("chars: column display name preserved", col.Title == "Catégorie & Über-col", col.Title);
            Program.Check("chars: view title preserved",
                tgt.Views.AsEnumerable().Any(v => v.Title == "Vue d'été & co"));
        }
    }

    private static async Task VerifyAsync(Core.Csom.SpConnection site, string targetTitle, string label)
    {
        using var sctx = site.CreateContext();
        using var tctx = site.CreateContext();
        var verifier = new CopyVerifier(sctx, tctx);
        var mismatches = await verifier.VerifyAsync(
            sctx.Web.Lists.GetByTitle(LibTitle),
            tctx.Web.Lists.GetByTitle(targetTitle),
            new[] { "CharCol" }, compareFileContent: true, compareUsers: false);
        foreach (var m in mismatches.Take(10)) Console.WriteLine($"    MISMATCH: {m}");
        Program.Check($"chars {label}: verification clean (names, content, fields)",
            mismatches.Count == 0, $"{mismatches.Count} mismatches");
    }
}

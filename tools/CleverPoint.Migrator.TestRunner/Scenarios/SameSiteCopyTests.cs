using CleverPoint.Migrator.Core.Csom;
using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using CleverPoint.Migrator.Core.Validation;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Same-site copies on the source tenant: list-to-list and library-to-library
/// in the migtest subsite, with full read-back verification.
/// </summary>
public static class SameSiteCopyTests
{
    private static readonly string[] ListCompareFields =
        { "Title", "TextCol", "NumberCol", "DateCol", "ChoiceCol", "NotesCol", "FlagCol", "MoneyCol", "LinkCol", "PersonCol", "LookupCol" };

    public static async Task ProvisionAsync()
    {
        var site = await TestAssets.EnsureTestSiteAsync(Program.Source);
        Program.TestSite = site;
        await TestAssets.RecreateSourceListAsync(site);
        await TestAssets.RecreateSourceLibraryAsync(site);
        Program.Check("provision test assets", true, site.SiteUrl);
    }

    public static async Task CopyListAsync()
    {
        var site = await RequireTestSiteAsync();
        using (var ctx = site.CreateContext())
        {
            await TestAssets.DeleteIfExistsAsync(ctx, "MigTest-Copy");
        }

        var options = new CopyOptions
        {
            TargetListTitle = "MigTest-Copy",
            TargetListUrl = "Lists/MigTestCopy",
        };
        var result = await CopyEngine.CopyListAsync(site, site, TestAssets.SourceListTitle, options);
        Console.WriteLine($"  copy result: {result.Summary()}");
        foreach (var r in result.Records.Where(r => r.Status is ItemCopyStatus.Failed or ItemCopyStatus.Warning))
            Console.WriteLine($"    [{r.Status}] {r.ItemType} {r.SourcePath}: {r.Message}");

        Program.Check("list copy: no failures", result.Failed == 0, result.Summary());
        Program.Check("list copy: items copied", result.Records.Count(r => r.ItemType == "Item" && r.Status == ItemCopyStatus.Copied) == 25,
            $"{result.Records.Count(r => r.ItemType == "Item" && r.Status == ItemCopyStatus.Copied)} items");
        Program.Check("list copy: folder copied", result.Records.Any(r => r.ItemType == "Folder" && r.Status == ItemCopyStatus.Copied));

        // Verify field-by-field.
        using var sourceCtx = site.CreateContext();
        using var targetCtx = site.CreateContext();
        var sourceList = sourceCtx.Web.Lists.GetByTitle(TestAssets.SourceListTitle);
        var targetList = targetCtx.Web.Lists.GetByTitle("MigTest-Copy");
        var verifier = new CopyVerifier(sourceCtx, targetCtx);
        var mismatches = await verifier.VerifyAsync(sourceList, targetList, ListCompareFields);
        foreach (var m in mismatches.Take(20)) Console.WriteLine($"    MISMATCH: {m}");
        Program.Check("list copy: verification clean", mismatches.Count == 0, $"{mismatches.Count} mismatches");

        // Views + formatting customizations.
        targetCtx.Load(targetList.Views, vs => vs.Include(v => v.Title, v => v.ViewQuery, v => v.CustomFormatter, v => v.RowLimit));
        var numberCol = targetList.Fields.GetByInternalNameOrTitle("NumberCol");
        targetCtx.Load(numberCol, f => f.CustomFormatter);
        await targetCtx.ExecuteQueryAsync();

        var flaggedView = targetList.Views.AsEnumerable().FirstOrDefault(v => v.Title == "Flagged Items");
        Program.Check("list copy: custom view copied", flaggedView != null,
            flaggedView == null ? "view 'Flagged Items' missing" : $"RowLimit={flaggedView.RowLimit}");
        Program.Check("list copy: view query preserved",
            flaggedView?.ViewQuery.Contains("FlagCol") == true, flaggedView?.ViewQuery);
        Program.Check("list copy: view formatting JSON copied",
            flaggedView?.CustomFormatter == TestAssets.ViewFormatJson);
        Program.Check("list copy: column formatting JSON copied",
            numberCol.CustomFormatter == TestAssets.NumberColFormatJson);

        // Attachments arrived with content and the dates stayed preserved
        // (the date checks are part of the field-by-field verification above).
        var attachRecords = result.Records.Count(r => r.ItemType == "Attachment" && r.Status == ItemCopyStatus.Copied);
        Program.Check("list copy: attachments copied", attachRecords == 2, $"{attachRecords} items with attachments");
        var attItems = targetList.GetItems(new CamlQuery { ViewXml = "<View><Query><Where><IsNotNull><FieldRef Name='Attachments'/></IsNotNull></Where></Query><RowLimit>50</RowLimit></View>" });
        targetCtx.Load(attItems, p => p.Include(i => i.AttachmentFiles.Include(a => a.FileName)));
        await targetCtx.ExecuteQueryAsync();
        var attachedFiles = attItems.AsEnumerable().SelectMany(i => i.AttachmentFiles.AsEnumerable()).Count();
        Program.Check("list copy: attachment files present on target", attachedFiles >= 2, $"{attachedFiles} files");
    }

    /// <summary>Explorer-style selection: copy only explicitly chosen item IDs.</summary>
    public static async Task CopySelectedItemsAsync()
    {
        var site = await RequireTestSiteAsync();
        using (var ctx = site.CreateContext())
        {
            await TestAssets.DeleteIfExistsAsync(ctx, "MigTest-SelCopy");
        }

        // Pick three real item IDs from the source list (like the explorer would).
        List<int> pickedIds;
        using (var ctx = site.CreateContext())
        {
            var list = ctx.Web.Lists.GetByTitle(TestAssets.SourceListTitle);
            var items = list.GetItems(new CamlQuery { ViewXml = "<View Scope='RecursiveAll'><RowLimit>20</RowLimit></View>" });
            ctx.Load(items, p => p.Include(i => i.Id, i => i.FileSystemObjectType));
            await ctx.ExecuteQueryAsync();
            pickedIds = items.AsEnumerable()
                .Where(i => i.FileSystemObjectType != FileSystemObjectType.Folder)
                .Take(3).Select(i => i.Id).ToList();
        }

        var options = new CopyOptions
        {
            TargetListTitle = "MigTest-SelCopy",
            TargetListUrl = "Lists/MigTestSelCopy",
            ItemIds = pickedIds,
        };
        var result = await CopyEngine.CopyListAsync(site, site, TestAssets.SourceListTitle, options);
        Console.WriteLine($"  copy result: {result.Summary()}");
        foreach (var r in result.Records.Where(r => r.Status is ItemCopyStatus.Failed or ItemCopyStatus.Warning))
            Console.WriteLine($"    [{r.Status}] {r.ItemType} {r.SourcePath}: {r.Message}");

        Program.Check("selected items: no failures", result.Failed == 0, result.Summary());
        var copied = result.Records.Count(r => r.ItemType == "Item" && r.Status == ItemCopyStatus.Copied);
        Program.Check("selected items: exactly the 3 picked items copied", copied == 3, $"{copied} items (picked IDs: {string.Join(",", pickedIds)})");

        using var targetCtx = site.CreateContext();
        var targetList = targetCtx.Web.Lists.GetByTitle("MigTest-SelCopy");
        targetCtx.Load(targetList, l => l.ItemCount);
        await targetCtx.ExecuteQueryAsync();
        Program.Check("selected items: target holds only the selection", targetList.ItemCount == 3, $"{targetList.ItemCount} on target");
    }

    /// <summary>Surgical mixed selection: 2 specific files + 1 subfolder via SelectedPaths.</summary>
    public static async Task CopySelectedPathsAsync()
    {
        var site = await RequireTestSiteAsync();
        using (var ctx = site.CreateContext())
        {
            await TestAssets.DeleteIfExistsAsync(ctx, "MigTest-PathCopy");
        }

        // Discover real paths like the explorer would: 2 root-level files + 1 subfolder.
        List<string> picked;
        int expectFiles, expectFolders;
        using (var ctx = site.CreateContext())
        {
            var list = ctx.Web.Lists.GetByTitle(TestAssets.SourceLibTitle);
            ctx.Load(list.RootFolder, f => f.ServerRelativeUrl);
            var items = list.GetItems(new CamlQuery { ViewXml = "<View Scope='RecursiveAll'><RowLimit>200</RowLimit></View>" });
            ctx.Load(items);
            ctx.Load(items, p => p.Include(i => i.Id, i => i.FileSystemObjectType));
            await ctx.ExecuteQueryAsync();
            var rootUrl = list.RootFolder.ServerRelativeUrl;
            var all = items.AsEnumerable()
                .Select(i => (Ref: (string)i["FileRef"], IsFolder: i.FileSystemObjectType == FileSystemObjectType.Folder))
                .ToList();
            var folderRef = all.First(x => x.IsFolder).Ref;
            // True root-level files only: nothing below any folder.
            var rootFiles = all.Where(x => !x.IsFolder && !x.Ref[(rootUrl.Length + 1)..].Contains('/'))
                .Take(2).Select(x => x.Ref).ToList();
            var inFolderFiles = all.Count(x => !x.IsFolder && x.Ref.StartsWith(folderRef + "/", StringComparison.OrdinalIgnoreCase));
            expectFolders = all.Count(x => x.IsFolder
                && (x.Ref.Equals(folderRef, StringComparison.OrdinalIgnoreCase)
                    || x.Ref.StartsWith(folderRef + "/", StringComparison.OrdinalIgnoreCase)));
            picked = rootFiles.Append(folderRef).ToList();
            expectFiles = rootFiles.Count + inFolderFiles;
            Console.WriteLine($"  picked: {string.Join(", ", picked.Select(p => p.Split('/')[^1]))} (expect {expectFiles} files, {expectFolders} folders)");
        }

        var options = new CopyOptions
        {
            TargetListTitle = "MigTest-PathCopy",
            TargetListUrl = "MigTestPathCopy",
            SelectedPaths = picked,
        };
        var result = await CopyEngine.CopyListAsync(site, site, TestAssets.SourceLibTitle, options);
        Console.WriteLine($"  copy result: {result.Summary()}");
        foreach (var r in result.Records.Where(r => r.Status is ItemCopyStatus.Failed or ItemCopyStatus.Warning))
            Console.WriteLine($"    [{r.Status}] {r.ItemType} {r.SourcePath}: {r.Message}");

        Program.Check("selected paths: no failures", result.Failed == 0, result.Summary());
        var copiedFiles = result.Records.Count(r => r.ItemType == "File" && r.Status == ItemCopyStatus.Copied);
        Program.Check($"selected paths: exactly {expectFiles} files copied (2 picked + folder contents)",
            copiedFiles == expectFiles, $"{copiedFiles} files");

        // The target must hold ONLY the selection: the 2 files, the folder, its children.
        using var targetCtx = site.CreateContext();
        var targetList = targetCtx.Web.Lists.GetByTitle("MigTest-PathCopy");
        var targetItems = targetList.GetItems(new CamlQuery { ViewXml = "<View Scope='RecursiveAll'><RowLimit>500</RowLimit></View>" });
        targetCtx.Load(targetItems, p => p.Include(i => i.FileSystemObjectType));
        await targetCtx.ExecuteQueryAsync();
        var tFiles = targetItems.AsEnumerable().Count(i => i.FileSystemObjectType == FileSystemObjectType.File);
        var tFolders = targetItems.AsEnumerable().Count(i => i.FileSystemObjectType == FileSystemObjectType.Folder);
        Program.Check("selected paths: target holds only the selection",
            tFiles == expectFiles && tFolders == expectFolders, $"{tFiles} files, {tFolders} folders on target");
    }

    public static async Task CopyLibraryAsync()
    {
        var site = await RequireTestSiteAsync();
        using (var ctx = site.CreateContext())
        {
            await TestAssets.DeleteIfExistsAsync(ctx, "MigTest-CopyLib");
        }

        var options = new CopyOptions
        {
            TargetListTitle = "MigTest-CopyLib",
            TargetListUrl = "MigTestCopyLib",
        };
        var result = await CopyEngine.CopyListAsync(site, site, TestAssets.SourceLibTitle, options);
        Console.WriteLine($"  copy result: {result.Summary()}");
        foreach (var r in result.Records.Where(r => r.Status is ItemCopyStatus.Failed or ItemCopyStatus.Warning))
            Console.WriteLine($"    [{r.Status}] {r.ItemType} {r.SourcePath}: {r.Message}");

        Program.Check("library copy: no failures", result.Failed == 0, result.Summary());
        Program.Check("library copy: 12 files copied",
            result.Records.Count(r => r.ItemType == "File" && r.Status == ItemCopyStatus.Copied) == 12);

        using var sourceCtx = site.CreateContext();
        using var targetCtx = site.CreateContext();
        var sourceList = sourceCtx.Web.Lists.GetByTitle(TestAssets.SourceLibTitle);
        var targetList = targetCtx.Web.Lists.GetByTitle("MigTest-CopyLib");
        var verifier = new CopyVerifier(sourceCtx, targetCtx);
        var mismatches = await verifier.VerifyAsync(sourceList, targetList,
            new[] { "DocCategory" }, compareFileContent: true, knownSourceHashes: result.FileHashes);
        foreach (var m in mismatches.Take(20)) Console.WriteLine($"    MISMATCH: {m}");
        Program.Check("library copy: verification clean (incl. SHA-256)", mismatches.Count == 0, $"{mismatches.Count} mismatches");
    }

    private static async Task<SpConnection> RequireTestSiteAsync()
    {
        if (Program.TestSite != null) return Program.TestSite;
        var site = await TestAssets.EnsureTestSiteAsync(Program.Source);
        Program.TestSite = site;
        return site;
    }
}

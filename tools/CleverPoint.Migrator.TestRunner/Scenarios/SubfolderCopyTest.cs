using CleverPoint.Migrator.Core.Csom;
using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>Verifies that TargetSubfolderRelative actually lands the copied files in
/// that subfolder of the target library, not the library root (the drag-onto-folder case).</summary>
public static class SubfolderCopyTest
{
    public static async Task RunAsync()
    {
        var site = Program.TestSite ?? await TestAssets.EnsureTestSiteAsync(Program.Source);
        Program.TestSite = site;
        const string targetTitle = "MigTest-SubfolderDrop";
        const string sub = "DropZone/Inner";

        using (var ctx = site.CreateContext()) await TestAssets.DeleteIfExistsAsync(ctx, targetTitle);

        var result = await CopyEngine.CopyListAsync(site, site, TestAssets.SourceLibTitle, new CopyOptions
        {
            TargetListTitle = targetTitle,
            TargetListUrl = "MigTestSubfolderDrop",
            TargetSubfolderRelative = sub,
        });
        Console.WriteLine($"  copy: {result.Summary()}");

        // Every copied File should sit under .../<targetUrl>/DropZone/Inner/...
        var listUrl = $"/sites/DemoLargeSite/{TestAssets.SubsiteLeaf}/MigTestSubfolderDrop";
        var expectedPrefix = $"{listUrl}/{sub}/";
        var files = result.Records.Where(r => r.ItemType == "File" && r.Status == ItemCopyStatus.Copied).ToList();
        var underSub = files.Count(f => f.TargetPath.Contains($"/{sub}/", StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"  {files.Count} file(s) copied, {underSub} under '{sub}'. sample: {files.FirstOrDefault().TargetPath}");
        Program.Check("subfolder copy: files land in the subfolder", files.Count > 0 && underSub == files.Count,
            $"{underSub}/{files.Count} under {sub}");

        // Confirm on the server: the subfolder exists and holds files.
        using var c = site.CreateContext();
        try
        {
            var folder = c.Web.GetFolderByServerRelativeUrl($"{listUrl}/{sub}");
            c.Load(folder, f => f.Exists, f => f.ItemCount);
            await c.ExecuteQueryAsync();
            Program.Check("subfolder copy: target subfolder exists with items", folder.Exists && folder.ItemCount > 0,
                $"exists={folder.Exists} itemCount={folder.ItemCount}");
        }
        catch (Exception ex) { Program.Check("subfolder copy: target subfolder exists", false, ex.Message); }
    }
}

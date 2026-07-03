using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Verifies the "Structure only (no content)" copy mode (CopyOptions.CopyContent=false):
/// the target list AND library are created with their columns + views, but ZERO
/// items/files are copied. Used to stand up empty lists/libraries before content.
/// </summary>
public static class StructureOnlyTest
{
    public static async Task RunAsync()
    {
        var site = Program.TestSite ?? await TestAssets.EnsureTestSiteAsync(Program.Source);
        Program.TestSite = site;

        // Make sure the source list + library exist WITH content, so "0 copied" is meaningful.
        await TestAssets.RecreateSourceListAsync(site);
        await TestAssets.RecreateSourceLibraryAsync(site);

        // ---- Generic LIST: structure only ----
        const string listTarget = "MigTest-StructOnly-List";
        using (var ctx = site.CreateContext()) await TestAssets.DeleteIfExistsAsync(ctx, listTarget);

        var listRes = await CopyEngine.CopyListAsync(site, site, TestAssets.SourceListTitle, new CopyOptions
        {
            TargetListTitle = listTarget,
            TargetListUrl = "Lists/MigTestStructOnlyList",
            CopyContent = false,
        });
        Console.WriteLine($"  list structure-only: {listRes.Summary()}");
        // Schema rows (columns/views/content types) legitimately copy; assert NO content did.
        var listContent = listRes.Records.Count(r => r.ItemType is "Item" or "File" or "Folder" && r.Status == ItemCopyStatus.Copied);
        Program.Check("structure-only list: no content items copied", listContent == 0, $"content records={listContent}");

        using (var c = site.CreateContext())
        {
            var l = c.Web.Lists.GetByTitle(listTarget);
            c.Load(l, x => x.ItemCount);
            c.Load(l.Fields, fs => fs.Include(f => f.InternalName));
            c.Load(l.Views, vs => vs.Include(v => v.Title));
            await c.ExecuteQueryAsync();
            var fields = l.Fields.AsEnumerable().Select(f => f.InternalName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var wanted = new[] { "TextCol", "NumberCol", "ChoiceCol", "LookupCol" };
            var haveCols = wanted.All(fields.Contains);
            var haveView = l.Views.AsEnumerable().Any(v => v.Title == "Flagged Items");
            Program.Check("structure-only list: created empty", l.ItemCount == 0, $"itemCount={l.ItemCount}");
            Program.Check("structure-only list: columns recreated", haveCols,
                $"missing: {string.Join(",", wanted.Where(w => !fields.Contains(w)))}");
            Program.Check("structure-only list: custom view recreated", haveView, "Flagged Items view present");
        }

        // ---- Document LIBRARY: structure only ----
        const string libTarget = "MigTest-StructOnly-Lib";
        using (var ctx = site.CreateContext()) await TestAssets.DeleteIfExistsAsync(ctx, libTarget);

        var libRes = await CopyEngine.CopyListAsync(site, site, TestAssets.SourceLibTitle, new CopyOptions
        {
            TargetListTitle = libTarget,
            TargetListUrl = "MigTestStructOnlyLib",
            CopyContent = false,
        });
        Console.WriteLine($"  library structure-only: {libRes.Summary()}");
        var libContent = libRes.Records.Count(r => r.ItemType is "Item" or "File" or "Folder" && r.Status == ItemCopyStatus.Copied);
        Program.Check("structure-only library: no files/folders copied", libContent == 0, $"content records={libContent}");

        using (var c = site.CreateContext())
        {
            var l = c.Web.Lists.GetByTitle(libTarget);
            c.Load(l, x => x.ItemCount);
            await c.ExecuteQueryAsync();
            Program.Check("structure-only library: created empty", l.ItemCount == 0, $"itemCount={l.ItemCount}");
        }
    }
}

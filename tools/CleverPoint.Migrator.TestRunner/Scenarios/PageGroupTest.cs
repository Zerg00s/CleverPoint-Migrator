using CleverPoint.Migrator.Core.Auth;
using CleverPoint.Migrator.Core.Csom;
using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Reproduces the user's modern-page copy that 406'd: LMAS Site Pages ->
/// the cleverpointlab M365 Group site Site Pages, content-only, single page.
/// </summary>
public static class PageGroupTest
{
    private const string SourceSite = "https://gocleverpointcom.sharepoint.com/sites/LMAS";
    private const string TargetSite = "https://cleverpointlab.sharepoint.com/sites/Migrationson365Group";

    public static async Task RunAsync()
    {
        var source = new SpConnection(SourceSite, new CertTokenProvider(Program.SourceCreds));
        var target = new SpConnection(TargetSite, new CertTokenProvider(Program.TargetCreds));

        // Confirm app-only auth reaches the Group site.
        try
        {
            using var doc = await target.Rest.GetJsonAsync($"{TargetSite}/_api/web?$select=Title");
            Program.Check("page-group: target reachable", true, doc.RootElement.GetProperty("Title").GetString());
        }
        catch (Exception ex)
        {
            Program.Check("page-group: target reachable", false, ex.Message);
            return;
        }

        var options = new CopyOptions
        {
            TargetListTitle = "Site Pages",
            MergeSchema = false,        // content-only into the existing library
            CopyContent = true,
            ExistingMode = ExistingItemMode.Overwrite,
            SelectedPaths = new() { $"{SourceSite}/SitePages/Modern.aspx" },
        };

        var result = await CopyEngine.CopyListAsync(source, target, "Site Pages", options);
        Console.WriteLine($"  result: {result.Summary()}");
        foreach (var r in result.Records)
            Console.WriteLine($"    [{r.Status}] {r.ItemType} {r.SourcePath}: {r.Message}");

        Program.Check("page-group: page copied", result.Records.Any(r => r.ItemType == "Page" && r.Status == ItemCopyStatus.Copied),
            result.Summary());

        // Read the target page back via the SitePages API: a real, rendering modern
        // page has a Site Page content type and non-empty CanvasContent1.
        try
        {
            using var listDoc = await target.Rest.GetJsonAsync($"{TargetSite}/_api/web/lists/GetByTitle('Site Pages')/items?$select=Id,FileLeafRef&$filter=FileLeafRef eq 'Modern.aspx'");
            var id = listDoc.RootElement.GetProperty("value")[0].GetProperty("Id").GetInt32();
            using var pageDoc = await target.Rest.GetJsonAsync($"{TargetSite}/_api/sitepages/pages({id})");
            var canvas = pageDoc.RootElement.TryGetProperty("CanvasContent1", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String ? c.GetString() ?? "" : "";
            Console.WriteLine($"  target page id {id}, CanvasContent1 length {canvas.Length}");
            Program.Check("page-group: target is a real site page with content", canvas.Length > 0, $"{canvas.Length} chars of canvas");
        }
        catch (Exception ex)
        {
            Program.Check("page-group: target is a real site page with content", false, ex.Message);
        }
    }
}

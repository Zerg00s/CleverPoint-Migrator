using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>Copies the BrokenOneNote library within DemoLargeSite and checks that
/// each topmost notebook folder (one with a .onetoc2) is marked on the target with
/// HTML_x0020_File_x0020_Type = OneNote.Notebook, while a plain folder is not.</summary>
public static class OneNoteCopyTest
{
    public static async Task RunAsync()
    {
        var site = Program.Source; // DemoLargeSite
        const string sourceTitle = "BrokenOneNote";
        const string targetTitle = "MigTest-OneNote";

        using (var ctx = site.CreateContext())
            await TestAssets.DeleteIfExistsAsync(ctx, targetTitle);

        var result = await CopyEngine.CopyListAsync(site, site, sourceTitle, new CopyOptions
        {
            TargetListTitle = targetTitle,
            TargetListUrl = "MigTestOneNote",
            PreserveAuthorsAndDates = true,
        });
        Console.WriteLine($"  copy: {result.Summary()}");

        // Read back HTML_x0020_File_x0020_Type for each top-level folder on the target.
        var listUrl = "/sites/DemoLargeSite/MigTestOneNote";
        using var doc = await site.Rest.GetJsonAsync(
            $"{site.SiteUrl}/_api/web/GetFolderByServerRelativeUrl('{Uri.EscapeDataString(listUrl)}')/Folders"
          + "?$expand=ListItemAllFields&$select=Name,ListItemAllFields/HTML_x0020_File_x0020_Type");

        var marked = new List<string>();
        var plain = new List<string>();
        foreach (var f in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            var name = f.GetProperty("Name").GetString() ?? "";
            if (name == "Forms") continue;
            string? type = null;
            if (f.TryGetProperty("ListItemAllFields", out var li) && li.ValueKind == System.Text.Json.JsonValueKind.Object
                && li.TryGetProperty("HTML_x0020_File_x0020_Type", out var ht) && ht.ValueKind == System.Text.Json.JsonValueKind.String)
                type = ht.GetString();
            Console.WriteLine($"    {name}: HTML_File_Type={type ?? "(none)"}");
            if (type == "OneNote.Notebook") marked.Add(name); else plain.Add(name);
        }

        Program.Check("onenote: 3 notebooks marked", marked.Count == 3, $"marked=[{string.Join(", ", marked)}]");
        Program.Check("onenote: Sample folder NOT marked", plain.Any(p => p.StartsWith("Sample")),
            $"plain=[{string.Join(", ", plain)}]");
    }
}

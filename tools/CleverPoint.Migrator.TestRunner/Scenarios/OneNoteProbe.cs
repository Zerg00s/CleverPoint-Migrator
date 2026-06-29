using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>Diagnostic: walk the BrokenOneNote library on DemoLargeSite and show
/// where the .onetoc2 files live, so we know at which folder level the explorer
/// should flag a OneNote notebook.</summary>
public static class OneNoteProbe
{
    public static async Task RunAsync()
    {
        var conn = Program.Source; // DemoLargeSite
        var root = "/sites/DemoLargeSite/BrokenOneNote";
        await WalkAsync(conn, root, 0);
        Program.Check("onenote probe ran", true);
    }

    private static async Task WalkAsync(Core.Csom.SpConnection conn, string folder, int depth)
    {
        if (depth > 4) return;
        var esc = Uri.EscapeDataString(folder.Replace("'", "''"));
        try
        {
            using var doc = await conn.Rest.GetJsonAsync(
                $"{conn.SiteUrl}/_api/web/GetFolderByServerRelativeUrl('{esc}')?$expand=Folders,Files&$select=Folders/Name,Folders/ServerRelativeUrl,Files/Name");
            var root = doc.RootElement;
            var pad = new string(' ', depth * 2);
            var files = Enum(root, "Files").Select(f => f.GetProperty("Name").GetString() ?? "").ToList();
            var toc = files.Where(n => n.EndsWith(".onetoc2", StringComparison.OrdinalIgnoreCase)).ToList();
            Console.WriteLine($"  {pad}{folder}  [files={files.Count}{(toc.Count > 0 ? ", ONETOC2=" + toc.Count : "")}]");
            foreach (var sub in Enum(root, "Folders"))
            {
                var srv = sub.GetProperty("ServerRelativeUrl").GetString() ?? "";
                if (srv.EndsWith("/Forms")) continue;
                await WalkAsync(conn, srv, depth + 1);
            }
        }
        catch (Exception ex) { Console.WriteLine($"  ERROR at {folder}: {ex.Message}"); }
    }

    private static IEnumerable<System.Text.Json.JsonElement> Enum(System.Text.Json.JsonElement parent, string prop)
    {
        if (!parent.TryGetProperty(prop, out var p)) yield break;
        var arr = p.ValueKind == System.Text.Json.JsonValueKind.Object && p.TryGetProperty("results", out var r) ? r : p;
        if (arr.ValueKind != System.Text.Json.JsonValueKind.Array) yield break;
        foreach (var e in arr.EnumerateArray()) yield return e;
    }
}

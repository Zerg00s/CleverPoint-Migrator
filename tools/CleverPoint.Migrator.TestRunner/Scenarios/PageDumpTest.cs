using CleverPoint.Migrator.Core.Auth;
using CleverPoint.Migrator.Core.Csom;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>Dumps a page's raw CanvasContent1 / LayoutWebpartsContent for diagnosis.</summary>
public static class PageDumpTest
{
    public static async Task RunAsync()
    {
        await DumpAsync("SOURCE", "https://gocleverpointcom.sharepoint.com/sites/LMAS", Program.SourceCreds);
        await DumpAsync("TARGET", "https://cleverpointlab.sharepoint.com/sites/Migrationson365Group", Program.TargetCreds);
        Program.Check("page-dump done", true);
    }

    private static async Task DumpAsync(string label, string site, AppCredentials creds)
    {
        var conn = new SpConnection(site, new CertTokenProvider(creds));
        try
        {
            using var listDoc = await conn.Rest.GetJsonAsync($"{site}/_api/web/lists/GetByTitle('Site Pages')/items?$select=Id,FileLeafRef&$filter=FileLeafRef eq 'Modern.aspx'");
            var arr = listDoc.RootElement.GetProperty("value");
            if (arr.GetArrayLength() == 0) { Console.WriteLine($"== {label}: no Modern.aspx =="); return; }
            var id = arr[0].GetProperty("Id").GetInt32();
            using var p = await conn.Rest.GetJsonAsync($"{site}/_api/sitepages/pages({id})");
            string Get(string k) => p.RootElement.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : (p.RootElement.TryGetProperty(k, out var v2) ? v2.ToString() : "");
            Console.WriteLine($"== {label} page id {id} ==");
            Console.WriteLine($"  PageLayoutType: {Get("PageLayoutType")}  PromotedState: {Get("PromotedState")}  ContentTypeId: {Get("ContentTypeId")}");
            var canvas = Get("CanvasContent1");
            var layout = Get("LayoutWebpartsContent");
            Console.WriteLine($"  CanvasContent1 ({canvas.Length}): {canvas[..Math.Min(900, canvas.Length)]}");
            Console.WriteLine($"  LayoutWebpartsContent ({layout.Length}): {layout[..Math.Min(500, layout.Length)]}");
        }
        catch (Exception ex) { Console.WriteLine($"== {label}: ERROR {ex.Message}"); }
    }
}

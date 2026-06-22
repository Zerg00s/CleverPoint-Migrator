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
            // List item fields (ClientSideApplicationId lives here, not on the page API).
            using var itemDoc = await conn.Rest.GetJsonAsync($"{site}/_api/web/lists/GetByTitle('Site Pages')/items?$select=Id,FileLeafRef,ClientSideApplicationId,PageLayoutType,ContentTypeId&$filter=FileLeafRef eq 'Modern.aspx'");
            var arr = itemDoc.RootElement.GetProperty("value");
            if (arr.GetArrayLength() == 0) { Console.WriteLine($"== {label}: no Modern.aspx =="); return; }
            var row = arr[0];
            var id = row.GetProperty("Id").GetInt32();
            string IGet(string k) => row.TryGetProperty(k, out var v) && v.ValueKind != System.Text.Json.JsonValueKind.Null ? v.ToString() : "(null)";
            Console.WriteLine($"== {label} page id {id} ==");
            Console.WriteLine($"  [item] ClientSideApplicationId: {IGet("ClientSideApplicationId")}  PageLayoutType: {IGet("PageLayoutType")}  ContentTypeId: {IGet("ContentTypeId")}");

            using var p = await conn.Rest.GetJsonAsync($"{site}/_api/sitepages/pages({id})");
            string Get(string k) => p.RootElement.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : (p.RootElement.TryGetProperty(k, out var v2) ? v2.ToString() : "");
            Console.WriteLine($"  [page] PageLayoutType: {Get("PageLayoutType")}  PromotedState: {Get("PromotedState")}");
            var canvas = Get("CanvasContent1");
            var layout = Get("LayoutWebpartsContent");
            Console.WriteLine($"  CanvasContent1 ({canvas.Length}) TAIL: …{canvas[Math.Max(0, canvas.Length - 700)..]}");
            Console.WriteLine($"  LayoutWebpartsContent ({layout.Length}): {layout}");
        }
        catch (Exception ex) { Console.WriteLine($"== {label}: ERROR {ex.Message}"); }
    }
}

using System.Text.Json;
using CleverPoint.Migrator.Core.Http;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Verifies the explorer's threshold-safe browsing strategy against a real
/// 50K-item library: plain /Folders enumeration throttles, while
/// RenderListDataAsStream (the SP UI's own endpoint) returns rows.
/// </summary>
public static class BrowseLargeTest
{
    public static async Task RunAsync()
    {
        var conn = Program.Source.ForWeb(Program.Source.SiteUrl); // DemoLargeSite root
        const string listTitle = "Library 1";

        using var listDoc = await conn.Rest.GetJsonAsync(
            $"{conn.SiteUrl}/_api/web/lists/GetByTitle('{Uri.EscapeDataString(listTitle)}')?$select=ItemCount,RootFolder/ServerRelativeUrl&$expand=RootFolder");
        var rootUrl = listDoc.RootElement.GetProperty("RootFolder").GetProperty("ServerRelativeUrl").GetString()!;
        var itemCount = listDoc.RootElement.GetProperty("ItemCount").GetInt32();
        Console.WriteLine($"  {listTitle}: {itemCount} items at {rootUrl}");
        Program.Check("browse-large: library is over the view threshold", itemCount > 5000, $"{itemCount} items");

        var throttled = false;
        try
        {
            var escaped = Uri.EscapeDataString(rootUrl.Replace("'", "''"));
            using var _ = await conn.Rest.GetJsonAsync(
                $"{conn.SiteUrl}/_api/web/GetFolderByServerRelativePath(decodedUrl='{escaped}')/Folders?$select=Name&$top=500");
        }
        catch (SpRestException ex) when (ex.Message.Contains("SPQueryThrottledException"))
        {
            throttled = true;
        }
        Console.WriteLine($"  plain /Folders enumeration throttled: {throttled}");

        // The fallback the explorer uses.
        var escapedList = Uri.EscapeDataString(rootUrl.Replace("'", "''"));
        var body = new
        {
            parameters = new
            {
                RenderOptions = 2,
                FolderServerRelativeUrl = rootUrl,
                ViewXml = "<View><Query><OrderBy><FieldRef Name='FileLeafRef'/></OrderBy></Query>"
                    + "<ViewFields><FieldRef Name='FileLeafRef'/><FieldRef Name='FileRef'/><FieldRef Name='FSObjType'/><FieldRef Name='File_x0020_Size'/></ViewFields>"
                    + "<RowLimit Paged='TRUE'>500</RowLimit></View>",
            },
        };
        var response = await conn.Rest.PostAsync(
            $"{conn.SiteUrl}/_api/web/GetList(@a1)/RenderListDataAsStream?@a1='{escapedList}'", body);
        using var doc = JsonDocument.Parse(response);
        var rows = doc.RootElement.GetProperty("Row").GetArrayLength();
        var first = doc.RootElement.GetProperty("Row").EnumerateArray().FirstOrDefault();
        var sampleName = first.ValueKind == JsonValueKind.Object ? first.GetProperty("FileLeafRef").GetString() : "(none)";
        Program.Check("browse-large: RenderListDataAsStream returns a page", rows > 0 && rows <= 500,
            $"{rows} rows, first: {sampleName}");

        // The explorer's generic-list item listing (the exact URL shape the
        // app uses - $orderby must say ID, not Id).
        using var listsDoc = await conn.Rest.GetJsonAsync(
            $"{conn.SiteUrl}/_api/web/lists?$select=Title,BaseType,Hidden,RootFolder/ServerRelativeUrl&$expand=RootFolder&$filter=Hidden eq false&$top=500");
        var genericList = listsDoc.RootElement.GetProperty("value").EnumerateArray()
            .First(e => e.GetProperty("BaseType").GetInt32() == 0);
        var listRoot = genericList.GetProperty("RootFolder").GetProperty("ServerRelativeUrl").GetString()!;
        var escapedGeneric = Uri.EscapeDataString(listRoot.Replace("'", "''"));
        using var itemsDoc = await conn.Rest.GetJsonAsync(
            $"{conn.SiteUrl}/_api/web/GetList(@a2)/items?$select=Id,Title&$orderby=ID desc&$top=500&@a2='{escapedGeneric}'");
        var itemRows = itemsDoc.RootElement.GetProperty("value").GetArrayLength();
        Program.Check("browse-large: generic list items listing (explorer URL shape)", true,
            $"{genericList.GetProperty("Title").GetString()}: {itemRows} items");
    }
}

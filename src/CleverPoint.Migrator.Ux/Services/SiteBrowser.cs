using System.Text.Json;
using CleverPoint.Migrator.Core.Csom;

namespace CleverPoint.Migrator.Ux.Services;

public record SpListInfo(string Title, string ServerRelativeUrl, bool IsLibrary, int ItemCount);
public record SpWebInfo(string Title, string Url);
public record SpFolderEntry(string Name, string ServerRelativeUrl, bool IsFolder, long Size, int ItemId = 0,
    string Created = "", string CreatedBy = "", string Modified = "", string ModifiedBy = "")
{
    /// <summary>UI selection state (source-pane checkbox).</summary>
    public bool Selected { get; set; }
}

/// <summary>
/// Read-only browsing for the explorer panes (ported from the WinForms app):
/// subsites, non-system lists/libraries, and folder/item contents. Threshold-safe
/// via RenderListDataAsStream so 100K-item libraries still enumerate. Large lists
/// page until a cap so the virtualized grid has plenty to scroll without an
/// unbounded REST crawl.
/// </summary>
public class SiteBrowser
{
    private readonly TimeSpan _ttl;
    public int MaxItemsLoaded { get; set; } = 5000;   // page until this many; grid virtualizes the rest

    public SiteBrowser(int cacheMinutes = 15) => _ttl = TimeSpan.FromMinutes(Math.Max(1, cacheMinutes));

    public async Task<List<SpListInfo>> GetListsAsync(SpConnection conn, bool useCache = true)
    {
        var cacheKey = $"lists_{Sanitize(conn.SiteUrl)}";
        if (useCache && TryReadCache<List<SpListInfo>>(cacheKey, out var cached)) return cached!;

        using var doc = await conn.Rest.GetJsonAsync(
            $"{conn.SiteUrl}/_api/web/lists?$select=Title,Hidden,BaseTemplate,BaseType,ItemCount,RootFolder/ServerRelativeUrl&$expand=RootFolder&$filter=Hidden eq false&$top=500");
        var lists = new List<SpListInfo>();
        foreach (var element in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            var template = element.GetProperty("BaseTemplate").GetInt32();
            if (template is 116 or 117 or 118 or 121 or 122 or 123 or 175 or 851 or 3300) continue;
            lists.Add(new SpListInfo(
                element.GetProperty("Title").GetString() ?? "",
                element.GetProperty("RootFolder").GetProperty("ServerRelativeUrl").GetString() ?? "",
                element.GetProperty("BaseType").GetInt32() == 1,
                element.GetProperty("ItemCount").GetInt32()));
        }
        lists = lists.OrderByDescending(l => l.IsLibrary).ThenBy(l => l.Title).ToList();
        WriteCache(cacheKey, lists);
        return lists;
    }

    public async Task<List<SpWebInfo>> GetSubwebsAsync(SpConnection conn, bool useCache = true)
    {
        var cacheKey = $"webs_{Sanitize(conn.SiteUrl)}";
        if (useCache && TryReadCache<List<SpWebInfo>>(cacheKey, out var cached)) return cached!;

        using var doc = await conn.Rest.GetJsonAsync($"{conn.SiteUrl}/_api/web/webs?$select=Title,Url&$top=200");
        var webs = doc.RootElement.GetProperty("value").EnumerateArray()
            .Select(e => new SpWebInfo(e.GetProperty("Title").GetString() ?? "", e.GetProperty("Url").GetString() ?? ""))
            .OrderBy(w => w.Title).ToList();
        WriteCache(cacheKey, webs);
        return webs;
    }

    /// <summary>Folder/library contents. Pages via RenderListDataAsStream up to MaxItemsLoaded.</summary>
    public async Task<List<SpFolderEntry>> GetFolderAsync(SpConnection conn, string folderServerRelativeUrl, string listServerRelativeUrl)
    {
        var entries = new List<SpFolderEntry>();
        string? paging = null;
        var listUrl = Uri.EscapeDataString(listServerRelativeUrl.Replace("'", "''"));
        do
        {
            var body = new
            {
                parameters = new
                {
                    RenderOptions = 2,
                    FolderServerRelativeUrl = folderServerRelativeUrl,
                    Paging = paging,
                    ViewXml = "<View Scope='RecursiveAll'><Query><OrderBy><FieldRef Name='FileLeafRef'/></OrderBy></Query>"
                        + "<ViewFields><FieldRef Name='FileLeafRef'/><FieldRef Name='FileRef'/><FieldRef Name='FSObjType'/><FieldRef Name='File_x0020_Size'/>"
                        + "<FieldRef Name='Created'/><FieldRef Name='Modified'/><FieldRef Name='Author'/><FieldRef Name='Editor'/></ViewFields>"
                        + "<RowLimit Paged='TRUE'>500</RowLimit></View>",
                },
            };
            var response = await conn.Rest.PostAsync(
                $"{conn.SiteUrl}/_api/web/GetList(@a1)/RenderListDataAsStream?@a1='{listUrl}'", body);
            using var doc = JsonDocument.Parse(response);
            foreach (var row in doc.RootElement.GetProperty("Row").EnumerateArray())
            {
                var name = row.GetProperty("FileLeafRef").GetString() ?? "";
                if (name == "Forms") continue;
                var isFolder = row.TryGetProperty("FSObjType", out var t) && t.GetString() == "1";
                var size = row.TryGetProperty("File_x0020_Size", out var s) && long.TryParse(s.GetString(), out var l) ? l : 0;
                entries.Add(new SpFolderEntry(name, row.GetProperty("FileRef").GetString() ?? "", isFolder, size,
                    Created: Friendly(row, "Created"), CreatedBy: Person(row, "Author"),
                    Modified: Friendly(row, "Modified"), ModifiedBy: Person(row, "Editor")));
            }
            paging = doc.RootElement.TryGetProperty("NextHref", out var nh) && nh.ValueKind == JsonValueKind.String
                ? nh.GetString()!.TrimStart('?') : null;
        } while (paging != null && entries.Count < MaxItemsLoaded);

        return entries.OrderByDescending(e => e.IsFolder).ThenBy(e => e.Name).ToList();
    }

    /// <summary>Generic-list items, paged up to MaxItemsLoaded (live IDs for selection).</summary>
    public async Task<List<SpFolderEntry>> GetListItemsAsync(SpConnection conn, SpListInfo list)
    {
        var escaped = Uri.EscapeDataString(list.ServerRelativeUrl.Replace("'", "''"));
        var items = new List<SpFolderEntry>();
        var url = $"{conn.SiteUrl}/_api/web/GetList(@a1)/items?$select=Id,Title,Created,Modified,Author/Title,Editor/Title&$expand=Author,Editor&$orderby=ID desc&$top=500&@a1='{escaped}'";
        while (url.Length > 0 && items.Count < MaxItemsLoaded)
        {
            using var doc = await conn.Rest.GetJsonAsync(url);
            foreach (var e in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                var id = e.GetProperty("Id").GetInt32();
                var title = e.TryGetProperty("Title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : $"(item {id})";
                items.Add(new SpFolderEntry(title, "", false, 0, id,
                    Created: ItemWhen(e, "Created"), CreatedBy: ItemWho(e, "Author"),
                    Modified: ItemWhen(e, "Modified"), ModifiedBy: ItemWho(e, "Editor")));
            }
            url = doc.RootElement.TryGetProperty("odata.nextLink", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "";
        }
        return items;
    }

    private static string ItemWhen(JsonElement e, string field) =>
        e.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String && DateTime.TryParse(v.GetString(), out var dt)
            ? dt.ToLocalTime().ToString("g") : "";
    private static string ItemWho(JsonElement e, string field) =>
        e.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Object && v.TryGetProperty("Title", out var t) ? t.GetString() ?? "" : "";
    private static string Friendly(JsonElement row, string field) =>
        row.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
    private static string Person(JsonElement row, string field) =>
        row.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0
            && v[0].TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";

    private static string Sanitize(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) ? c : '_'));

    private bool TryReadCache<T>(string key, out T? value)
    {
        value = default;
        try
        {
            var path = Path.Combine(UxSettings.CacheFolder, key + ".json");
            if (!File.Exists(path) || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > _ttl) return false;
            value = JsonSerializer.Deserialize<T>(File.ReadAllText(path));
            return value != null;
        }
        catch { return false; }
    }

    private static void WriteCache<T>(string key, T value)
    {
        try
        {
            Directory.CreateDirectory(UxSettings.CacheFolder);
            File.WriteAllText(Path.Combine(UxSettings.CacheFolder, key + ".json"), JsonSerializer.Serialize(value));
        }
        catch { /* best-effort */ }
    }
}

using System.Text.Json;
using CleverPoint.Migrator.Core.Csom;

namespace CleverPoint.Migrator.App.Services;

public record SpListInfo(string Title, string ServerRelativeUrl, bool IsLibrary, int ItemCount);
public record SpWebInfo(string Title, string Url);
public record SpFolderEntry(string Name, string ServerRelativeUrl, bool IsFolder, long Size);

/// <summary>
/// Read-only browsing for the explorer panes: subsites, non-system lists and
/// libraries, and folder contents. Results cache to disk for snappy
/// navigation (Settings > Maintenance > Clear cache empties it).
/// </summary>
public class SiteBrowser
{
    private readonly TimeSpan _ttl;

    public SiteBrowser(int cacheMinutes = 15)
    {
        _ttl = TimeSpan.FromMinutes(Math.Max(1, cacheMinutes));
    }

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
            // Catalogs and infrastructure lists stay out of the way.
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

    public async Task<List<SpFolderEntry>> GetFolderAsync(SpConnection conn, string folderServerRelativeUrl)
    {
        var escaped = Uri.EscapeDataString(folderServerRelativeUrl.Replace("'", "''"));
        using var folders = await conn.Rest.GetJsonAsync(
            $"{conn.SiteUrl}/_api/web/GetFolderByServerRelativePath(decodedUrl='{escaped}')/Folders?$select=Name,ServerRelativeUrl&$top=500");
        using var files = await conn.Rest.GetJsonAsync(
            $"{conn.SiteUrl}/_api/web/GetFolderByServerRelativePath(decodedUrl='{escaped}')/Files?$select=Name,ServerRelativeUrl,Length&$top=500");

        var entries = folders.RootElement.GetProperty("value").EnumerateArray()
            .Where(e => e.GetProperty("Name").GetString() != "Forms")
            .Select(e => new SpFolderEntry(e.GetProperty("Name").GetString() ?? "",
                e.GetProperty("ServerRelativeUrl").GetString() ?? "", true, 0))
            .Concat(files.RootElement.GetProperty("value").EnumerateArray()
                .Select(e => new SpFolderEntry(e.GetProperty("Name").GetString() ?? "",
                    e.GetProperty("ServerRelativeUrl").GetString() ?? "", false,
                    long.TryParse(e.GetProperty("Length").GetString(), out var l) ? l : 0)))
            .ToList();
        return entries;
    }

    private static string Sanitize(string s) =>
        string.Concat(s.Select(c => char.IsLetterOrDigit(c) ? c : '_'));

    private bool TryReadCache<T>(string key, out T? value)
    {
        value = default;
        try
        {
            var path = Path.Combine(AppSettings.CacheFolder, key + ".json");
            if (!File.Exists(path) || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > _ttl) return false;
            value = JsonSerializer.Deserialize<T>(File.ReadAllText(path));
            return value != null;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteCache<T>(string key, T value)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.CacheFolder);
            File.WriteAllText(Path.Combine(AppSettings.CacheFolder, key + ".json"), JsonSerializer.Serialize(value));
        }
        catch { /* cache is best-effort */ }
    }
}

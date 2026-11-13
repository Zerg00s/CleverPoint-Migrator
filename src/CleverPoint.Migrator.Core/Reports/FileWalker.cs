using System.Text;
using System.Text.Json;
using CleverPoint.Migrator.Core.Csom;
using CleverPoint.Migrator.Core.Http;

namespace CleverPoint.Migrator.Core.Reports;

/// <summary>One file or folder of a library or list, as a scan sees it.</summary>
public sealed record FileEntry(
    int Id, string Name, string Path, bool IsFolder, long Size, DateTime? Created, DateTime? Modified,
    string CreatedBy, string ModifiedBy, string Version, string ArchiveStatus, DateTime? ArchiveChanged, string HtmlFileType,
    long TotalSize = 0, long StreamSize = 0)
{
    /// <summary>
    /// What the entry takes in storage, older versions included, as SharePoint accounts it. For a folder:
    /// everything inside it. 0 when the library does not report it.
    /// </summary>
    public long StorageBytes => TotalSize > 0 ? TotalSize : Size;
    /// <summary>More than the one version every file has.</summary>
    public bool HasHistory => Version.Length > 0 && Version is not ("1.0" or "0.1");
    /// <summary>What the older versions of a file take. A file with a single version has none.</summary>
    public long VersionBytes => !IsFolder && HasHistory && TotalSize > StreamSize && StreamSize > 0 ? TotalSize - StreamSize : 0;
    /// <summary>The number of major versions, which is how many versions a library without drafts keeps.</summary>
    public int MajorVersions => int.TryParse(Version.Split('.')[0], out var major) && major > 0 ? major : 1;
    public string Extension => IsFolder ? "" : System.IO.Path.GetExtension(Name).TrimStart('.').ToLowerInvariant();
    /// <summary>The folder the entry is in, server-relative.</summary>
    public string Folder => Path.Contains('/') ? Path[..Path.LastIndexOf('/')] : "";
}

/// <summary>
/// Reads every file and folder of a library, however large. It pages through the library in the order
/// SharePoint stores it, with no filter and no sort, which is what keeps it clear of the 5,000-item
/// list view threshold.
/// </summary>
public static class FileWalker
{
    private const string CoreFields =
        "<FieldRef Name='ID'/><FieldRef Name='FileLeafRef'/><FieldRef Name='FileRef'/><FieldRef Name='FSObjType'/><FieldRef Name='File_x0020_Size'/>" +
        "<FieldRef Name='Created'/><FieldRef Name='Modified'/><FieldRef Name='Author'/><FieldRef Name='Editor'/><FieldRef Name='_UIVersionString'/>" +
        "<FieldRef Name='HTML_x0020_File_x0020_Type'/>";

    /// <summary>
    /// Storage as SharePoint accounts it: the total with every version, and the current version alone.
    /// They ride along with each row, which is what makes version storage cheap to report.
    /// </summary>
    private const string StorageFields = "<FieldRef Name='SMTotalSize'/><FieldRef Name='SMTotalFileStreamSize'/>";

    /// <summary>Only tenants with Microsoft 365 Archive have these; a library without them refuses a query that names them.</summary>
    private const string ArchiveFields = "<FieldRef Name='_FileArchiveStatus'/><FieldRef Name='_FileArchiveTimeLastModified'/>";

    /// <param name="webUrl">The web the list is in.</param>
    /// <param name="listId">The list's ID.</param>
    /// <param name="onProgress">Called after each page with how many entries have been read so far.</param>
    public static async Task ForEachAsync(SpConnection connection, string webUrl, string listId, Action<FileEntry> onEntry, CancellationToken ct,
        int pageSize = 5000, Action<long>? onProgress = null)
    {
        long read = 0;
        await foreach (var page in PagesAsync(connection, webUrl, listId, ct, pageSize))
        {
            foreach (var entry in page) onEntry(entry);
            read += page.Count;
            onProgress?.Invoke(read);
        }
    }

    /// <summary>The text reports show while a library is being read: its name and how far the read has got.</summary>
    public static Action<long> Progress(ReportContext context, string library, long items, string? side = null) =>
        read => context.Phase($"{(side is null ? "" : side + ": ")}{library}, {Math.Min(read, Math.Max(items, read)):N0} of {Math.Max(items, read):N0} items");

    /// <summary>The same walk a page at a time, for work that has to call SharePoint again for what it finds.</summary>
    public static async IAsyncEnumerable<List<FileEntry>> PagesAsync(SpConnection connection, string webUrl, string listId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct, int pageSize = 5000)
    {
        // 2 = with the archive and storage fields, 1 = storage fields only, 0 = neither.
        var extras = 2;
        string? paging = null;
        do
        {
            ct.ThrowIfCancellationRequested();
            string response;
            while (true)
            {
                try
                {
                    response = await PageAsync(connection, webUrl, listId, extras, paging, pageSize);
                    break;
                }
                catch (SpRestException ex) when (extras > 0 && paging is null && !Gone.Is(ex))
                {
                    // Most likely one of the extra fields is unknown in this library. Ask again with
                    // fewer; a real problem with the library fails the plain request too and is reported.
                    extras--;
                }
            }
            using var page = JsonDocument.Parse(response);
            var entries = new List<FileEntry>();
            if (page.RootElement.TryGetProperty("Row", out var rows) && rows.ValueKind == JsonValueKind.Array)
                foreach (var row in rows.EnumerateArray())
                {
                    var name = row.Str("FileLeafRef");
                    if (name.Length == 0) continue;
                    entries.Add(new FileEntry(
                        Id: (int)row.Num("ID"),
                        Name: name,
                        Path: row.Str("FileRef"),
                        IsFolder: row.Str("FSObjType") == "1",
                        Size: row.Num("File_x0020_Size"),
                        Created: row.When("Created"),
                        Modified: row.When("Modified"),
                        CreatedBy: Person(row, "Author"),
                        ModifiedBy: Person(row, "Editor"),
                        Version: row.Str("_UIVersionString"),
                        ArchiveStatus: row.Str("_FileArchiveStatus"),
                        ArchiveChanged: LookupDate(row, "_FileArchiveTimeLastModified"),
                        HtmlFileType: row.Str("HTML_x0020_File_x0020_Type"),
                        TotalSize: row.Num("SMTotalSize"),
                        StreamSize: row.Num("SMTotalFileStreamSize")));
                }
            yield return entries;
            paging = page.RootElement.TryGetProperty("NextHref", out var next) && next.ValueKind == JsonValueKind.String
                ? next.GetString()!.TrimStart('?')
                : null;
        } while (!string.IsNullOrEmpty(paging));
    }

    private static Task<string> PageAsync(SpConnection connection, string webUrl, string listId, int extras, string? paging, int pageSize)
    {
        var body = new
        {
            parameters = new
            {
                RenderOptions = 2,
                DatesInUtc = true,       // ISO dates instead of text in the site's regional format
                Paging = paging,
                ViewXml = "<View Scope='RecursiveAll'><Query></Query><ViewFields>" + CoreFields + (extras >= 1 ? StorageFields : "") + (extras >= 2 ? ArchiveFields : "")
                          + $"</ViewFields><RowLimit Paged='TRUE'>{pageSize}</RowLimit></View>",
            },
        };
        return connection.Rest.PostAsync($"{webUrl}/_api/web/lists(guid'{listId}')/RenderListDataAsStream", body);
    }

    /// <summary>A person field comes back as a list of people, or as plain text on some lists.</summary>
    private static string Person(JsonElement row, string field)
    {
        if (!row.TryGetProperty(field, out var value)) return "";
        if (value.ValueKind == JsonValueKind.Array)
        {
            var first = value.EnumerateArray().FirstOrDefault();
            return first.ValueKind == JsonValueKind.Object ? first.Str("title") : "";
        }
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    }

    /// <summary>A date that arrives as text, as a lookup object or as a list of one.</summary>
    private static DateTime? LookupDate(JsonElement row, string field)
    {
        if (!row.TryGetProperty(field, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Array) value = value.EnumerateArray().FirstOrDefault();
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Object => value.Str("lookupValue"),
            _ => null,
        };
        return DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var date) && date.Year > 1900
            ? date : null;
    }

    /// <summary>Document libraries of a web: ID, title, root folder, item count.</summary>
    public static async Task<List<(string Id, string Title, string RootFolder, long Items, long Template)>> LibrariesAsync(
        SpConnection connection, string webUrl, bool includeHidden, CancellationToken ct)
    {
        var lists = await connection.Rest.GetAllAsync(
            $"{webUrl}/_api/web/lists?$select=Id,Title,Hidden,ItemCount,BaseTemplate,BaseType,RootFolder/ServerRelativeUrl&$expand=RootFolder&$filter=BaseType eq 1&$top=500", ct);
        return lists
            .Where(l => includeHidden || !l.Bool("Hidden"))
            .Select(l => (l.Str("Id"), l.Str("Title"), l.Child("RootFolder").Str("ServerRelativeUrl"), l.Num("ItemCount"), l.Num("BaseTemplate")))
            .ToList();
    }
}

/// <summary>
/// Several GET requests in one round trip (SharePoint's $batch). One batch counts once against
/// throttling, which is what makes asking for the versions of every file affordable.
/// </summary>
public static class RestBatch
{
    public const int MaxPerBatch = 50;

    /// <summary>The status and body of each request, in the order asked.</summary>
    public static async Task<List<(int Status, string Body)>> GetAsync(SpConnection connection, string webUrl, IReadOnlyList<string> urls)
    {
        if (urls.Count == 0) return new();
        if (urls.Count > MaxPerBatch) throw new ArgumentException($"A batch holds at most {MaxPerBatch} requests.");
        var boundary = "batch_" + Guid.NewGuid().ToString("N");
        var body = new StringBuilder();
        foreach (var url in urls)
        {
            // Every line of the envelope has to end in CRLF.
            body.Append("--").Append(boundary).Append("\r\n")
                .Append("Content-Type: application/http\r\n")
                .Append("Content-Transfer-Encoding: binary\r\n\r\n")
                .Append("GET ").Append(url).Append(" HTTP/1.1\r\n")
                .Append("Accept: application/json;odata=nometadata\r\n\r\n");
        }
        body.Append("--").Append(boundary).Append("--\r\n");
        var raw = await connection.Rest.SendAsync(HttpMethod.Post, $"{webUrl}/_api/$batch", body.ToString(), null, $"multipart/mixed; boundary={boundary}");
        return Parse(raw);
    }

    private static List<(int Status, string Body)> Parse(string raw)
    {
        var results = new List<(int, string)>();
        var firstLine = raw.IndexOf('\n');
        if (firstLine < 0) return results;
        var separator = raw[..firstLine].TrimEnd('\r').Trim();
        if (!separator.StartsWith("--")) return results;
        foreach (var part in raw.Split(separator, StringSplitOptions.None))
        {
            if (string.IsNullOrWhiteSpace(part) || part.StartsWith("--")) continue;
            // A part is: its own headers, a blank line, then a complete HTTP response.
            var inner = AfterBlankLine(part.TrimStart('\r', '\n'));
            if (inner is null) continue;
            var statusEnd = inner.IndexOf('\n');
            if (statusEnd < 0) { results.Add((0, "")); continue; }
            var tokens = inner[..statusEnd].Trim().Split(' ', 3);
            var status = tokens.Length >= 2 && int.TryParse(tokens[1], out var code) ? code : 0;
            results.Add((status, (AfterBlankLine(inner) ?? "").Trim()));
        }
        return results;
    }

    private static string? AfterBlankLine(string text)
    {
        var at = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (at >= 0) return text[(at + 4)..];
        at = text.IndexOf("\n\n", StringComparison.Ordinal);
        return at >= 0 ? text[(at + 2)..] : null;
    }
}

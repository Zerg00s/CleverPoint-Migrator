using CleverPoint.Migrator.Core.Csom;
using CleverPoint.Migrator.Core.Model;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.Core.Operations;

/// <summary>
/// Modern page migration (Site Pages libraries). Modern pages are NOT plain
/// files: the .aspx is a stub and the real content lives in list item fields
/// (CanvasContent1, LayoutWebpartsContent, PageLayoutType...). Pages are
/// recreated on the target via AddTemplateFile(ClientSidePage), the fields
/// copied with absolute/server-relative URLs rewritten to the target web,
/// authors/dates preserved, then published.
/// </summary>
public class PageCopier
{
    private static readonly string[] PageFields =
    {
        "Title", "CanvasContent1", "LayoutWebpartsContent", "PageLayoutType",
        "PromotedState", "Description", "BannerImageUrl", "_TopicHeader",
    };

    private readonly SpConnection _source;
    private readonly SpConnection _target;
    private readonly ExistingItemMode _mode;

    /// <summary>When set, only these page file names (e.g. "Modern.aspx") are copied.</summary>
    public HashSet<string>? IncludeNames { get; set; }

    public PageCopier(SpConnection source, SpConnection target, ExistingItemMode mode = ExistingItemMode.Overwrite)
    {
        _mode = mode;
        _source = source;
        _target = target;
    }

    public async Task<CopyResult> CopyPagesAsync(CopyResult? liveResult = null, CancellationToken ct = default)
    {
        var result = liveResult ?? new CopyResult();
        using var sourceCtx = _source.CreateContext();
        using var targetCtx = _target.CreateContext();

        var sourceList = sourceCtx.Web.Lists.GetByTitle("Site Pages");
        var targetList = targetCtx.Web.Lists.GetByTitle("Site Pages");
        sourceCtx.Load(sourceCtx.Web, w => w.ServerRelativeUrl, w => w.Url);
        targetCtx.Load(targetCtx.Web, w => w.ServerRelativeUrl, w => w.Url);
        sourceCtx.Load(sourceList.RootFolder, f => f.ServerRelativeUrl);
        targetCtx.Load(targetList.RootFolder, f => f.ServerRelativeUrl);
        await sourceCtx.ExecuteQueryAsync();
        await targetCtx.ExecuteQueryAsync();

        var users = new UserResolver(sourceCtx, targetCtx);
        await users.PrimeSourceUsersAsync();

        // Existing target pages with their Modified date (skip / overwrite / if-newer).
        var targetPages = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var existingQuery = targetList.GetItems(CamlQuery.CreateAllItemsQuery(500));
        targetCtx.Load(existingQuery);
        await targetCtx.ExecuteQueryAsync();
        foreach (var item in existingQuery)
        {
            var n = ((string)item["FileRef"]).Split('/')[^1];
            targetPages[n] = item["Modified"] is DateTime dt ? dt : DateTime.MinValue;
        }

        var pages = sourceList.GetItems(CamlQuery.CreateAllItemsQuery(500));
        sourceCtx.Load(pages);
        await sourceCtx.ExecuteQueryAsync();

        foreach (var page in pages.AsEnumerable().Where(p => p.FileSystemObjectType == FileSystemObjectType.File))
        {
            ct.ThrowIfCancellationRequested();
            var fileRef = (string)page["FileRef"];
            var name = fileRef.Split('/')[^1];
            if (!name.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase)) continue;
            if (IncludeNames is not null && !IncludeNames.Contains(name)) continue;

            var targetUrl = $"{targetList.RootFolder.ServerRelativeUrl}/{name}";
            if (targetPages.TryGetValue(name, out var targetModified))
            {
                if (_mode == ExistingItemMode.Skip)
                {
                    result.Add("Page", fileRef, targetUrl, ItemCopyStatus.Skipped, "page already exists (skip mode)");
                    continue;
                }
                if (_mode == ExistingItemMode.CopyIfNewer)
                {
                    var sourceModified = page["Modified"] is DateTime sm ? sm : DateTime.MaxValue;
                    if (sourceModified <= targetModified)
                    {
                        result.Add("Page", fileRef, targetUrl, ItemCopyStatus.Skipped, "target page is already up to date");
                        continue;
                    }
                }
            }

            try
            {
                // Read the page through the SitePages REST API: it returns
                // CanvasContent1 as RAW JSON. (Reading the list item gives an
                // HTML-entity-encoded copy, and writing canvas as a plain
                // field goes through the HTML sanitizer, which strips link
                // hrefs. Both verified live.)
                using var sourcePage = await GetPageWithRetryAsync(_source, sourceCtx.Web.Url.TrimEnd('/'), page.Id);
                string? Get(string prop) => sourcePage.RootElement.TryGetProperty(prop, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;

                // We only reach here when the page is NOT a known target item, so
                // any file already at this URL is an orphan: either a checked-out
                // draft or (the common case) a non-site-page .aspx left by an earlier
                // generic-file-copy attempt, which makes the SitePages API reject it
                // ("does not have the site page content type"). Clear it first so
                // AddTemplateFile creates a real client-side page.
                await TryDeleteFileAsync(targetCtx, targetUrl);

                var stub = targetCtx.Web.GetFolderByServerRelativePath(ResourcePath.FromDecodedUrl(targetList.RootFolder.ServerRelativeUrl))
                    .Files.AddTemplateFile(targetUrl, TemplateFileType.ClientSidePage);
                targetCtx.Load(stub, f => f.ServerRelativeUrl);
                await targetCtx.ExecuteQueryAsync();
                if (!stub.ServerRelativeUrl.Equals(targetUrl, StringComparison.OrdinalIgnoreCase))
                {
                    // Name occupied (likely a checked-out page); undo the
                    // renamed stub and report.
                    await TryDeleteFileAsync(targetCtx, stub.ServerRelativeUrl);
                    result.Add("Page", fileRef, targetUrl, ItemCopyStatus.Warning,
                        "target name is held by a checked-out page; enable overwrite to replace it");
                    continue;
                }

                var stubItem = targetCtx.Web.GetFileByServerRelativePath(ResourcePath.FromDecodedUrl(targetUrl)).ListItemAllFields;
                targetCtx.Load(stubItem, i => i.Id);
                await targetCtx.ExecuteQueryAsync();
                var targetWebUrl = targetCtx.Web.Url.TrimEnd('/');
                var pageId = stubItem.Id;

                // Canvas + metadata through SavePageAsDraft (the supported
                // authoring surface; stores the canvas raw).
                var payload = new Dictionary<string, object?>
                {
                    ["__metadata"] = new Dictionary<string, string> { ["type"] = "SP.Publishing.SitePage" },
                    ["Title"] = Get("Title") ?? Path.GetFileNameWithoutExtension(name),
                    ["CanvasContent1"] = RewriteUrls(Get("CanvasContent1") ?? "", sourceCtx.Web, targetCtx.Web),
                    ["LayoutWebpartsContent"] = RewriteUrls(Get("LayoutWebpartsContent") ?? "", sourceCtx.Web, targetCtx.Web),
                    ["BannerImageUrl"] = RewriteUrls(Get("BannerImageUrl") ?? "", sourceCtx.Web, targetCtx.Web),
                    ["Description"] = Get("Description"),
                    ["TopicHeader"] = Get("TopicHeader"),
                };
                // The pages API needs an editing session: checkout -> save -> publish.
                await _target.Rest.PostAsync($"{targetWebUrl}/_api/sitepages/pages({pageId})/checkoutpage");
                await _target.Rest.PostRawAsync(
                    $"{targetWebUrl}/_api/sitepages/pages({pageId})/SavePageAsDraft",
                    System.Text.Json.JsonSerializer.Serialize(payload));
                // Rename-back BEFORE publish (a post-publish move creates a draft).
                await EnsureFileNameAsync(targetCtx, targetList, pageId, targetUrl);

                // Authors and dates next (any write after publish leaves a
                // draft minor version behind), then publish LAST.
                var item = targetCtx.Web.GetFileByServerRelativePath(ResourcePath.FromDecodedUrl(targetUrl)).ListItemAllFields;
                targetCtx.Load(item, i => i.Id);
                await targetCtx.ExecuteQueryAsync();
                var authorId = page.FieldValues.GetValueOrDefault("Author") is FieldUserValue av
                    ? await users.ResolveAsync(av.LookupId) : null;
                var editorId = page.FieldValues.GetValueOrDefault("Editor") is FieldUserValue ev
                    ? await users.ResolveAsync(ev.LookupId) : null;
                if (authorId.HasValue) item["Author"] = new FieldUserValue { LookupId = authorId.Value };
                if (editorId.HasValue) item["Editor"] = new FieldUserValue { LookupId = editorId.Value };
                item["Created"] = ItemCopier.ToWriteDate(page["Created"]);
                item["Modified"] = ItemCopier.ToWriteDate(page["Modified"]);
                item.UpdateOverwriteVersion();
                await targetCtx.ExecuteQueryAsync();

                await _target.Rest.PostAsync($"{targetWebUrl}/_api/sitepages/pages({pageId})/publish");

                result.Add("Page", fileRef, targetUrl, ItemCopyStatus.Copied,
                    targetPages.ContainsKey(name) ? "overwritten" : null);
            }
            catch (Exception ex)
            {
                result.Add("Page", fileRef, targetUrl, ItemCopyStatus.Failed, ex.Message);
            }
        }

        result.FinishedUtc = DateTime.UtcNow;
        return result;
    }

    /// <summary>
    /// SavePageAsDraft RENAMES the file to a Title-derived name on first
    /// save, even colliding with the page's own current name (which appends
    /// "(1)"). Verified live; the root of a long ghost hunt. This moves the
    /// file back to the intended URL when that happened.
    /// </summary>
    public static async Task EnsureFileNameAsync(ClientContext ctx, List pagesList, int pageId, string intendedUrl)
    {
        var item = pagesList.GetItemById(pageId);
        ctx.Load(item);
        await ctx.ExecuteQueryAsync();
        var actualRef = (string)item["FileRef"];
        if (!actualRef.Equals(intendedUrl, StringComparison.OrdinalIgnoreCase))
        {
            ctx.Web.GetFileByServerRelativePath(ResourcePath.FromDecodedUrl(actualRef)).MoveTo(intendedUrl, MoveOperations.Overwrite);
            await ctx.ExecuteQueryAsync();
        }
    }

    private static string Esc(string s) => Uri.EscapeDataString(s).Replace("'", "''");

    /// <summary>
    /// Deletes a file by URL, tolerating absence. Checked-out orphans get an
    /// UndoCheckOut first (delete fails on them otherwise). Returns what
    /// happened, for diagnostics.
    /// </summary>
    public static async Task<string> TryDeleteFileAsync(ClientContext ctx, string serverRelativeUrl)
    {
        try
        {
            ctx.Web.GetFileByServerRelativePath(ResourcePath.FromDecodedUrl(serverRelativeUrl)).DeleteObject();
            await ctx.ExecuteQueryAsync();
            return "deleted";
        }
        catch (ServerException first)
        {
            if (first.Message.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
                || first.Message.Contains("Not Found", StringComparison.OrdinalIgnoreCase)
                || first.Message.Contains("File Not Found", StringComparison.OrdinalIgnoreCase))
                return "absent";
            try
            {
                var file = ctx.Web.GetFileByServerRelativePath(ResourcePath.FromDecodedUrl(serverRelativeUrl));
                file.UndoCheckOut();
                file.DeleteObject();
                await ctx.ExecuteQueryAsync();
                return "undocheckout+deleted";
            }
            catch (ServerException second)
            {
                return $"failed: {first.Message} / {second.Message}";
            }
        }
    }

    /// <summary>The pages API can lag a fresh page; retry briefly on 404. Addressed by list item id.</summary>
    private static async Task<System.Text.Json.JsonDocument> GetPageWithRetryAsync(SpConnection conn, string webUrl, int itemId)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await conn.Rest.GetJsonAsync($"{webUrl}/_api/sitepages/pages({itemId})");
            }
            catch (Http.SpRestException ex) when (ex.StatusCode == 404 && attempt < 5)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
            }
        }
    }

    /// <summary>Rewrites absolute and server-relative source-web URLs to the target web.</summary>
    private static string RewriteUrls(string content, Web sourceWeb, Web targetWeb)
    {
        if (string.IsNullOrEmpty(content)) return content;
        return content
            .Replace(sourceWeb.Url, targetWeb.Url, StringComparison.OrdinalIgnoreCase)
            .Replace(sourceWeb.ServerRelativeUrl, targetWeb.ServerRelativeUrl, StringComparison.OrdinalIgnoreCase);
    }
}

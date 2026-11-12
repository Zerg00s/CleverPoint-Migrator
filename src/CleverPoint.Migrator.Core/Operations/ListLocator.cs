using CleverPoint.Migrator.Core.Csom;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.Core.Operations;

/// <summary>
/// Finds a list without trusting its display title.
///
/// SharePoint translates the titles of built-in lists. A German site calls its library "Dokumente", and
/// any site reports that library as "Dokumente" to a signed-in user whose profile language is German,
/// even when the site itself is English and stores "Documents". GetByTitle, however, only ever matches
/// the title in the SITE's default language. So a title read from one call can fail in the next:
/// "List 'Dokumente' does not exist at site with URL ...". That produced copies which created the
/// folders (list found by enumeration) and then no files (workers used GetByTitle).
///
/// The root folder path does not change with language, so it is the preferred key. The title is the
/// fallback for callers that only have one (older saved runs), matched both ways: as the caller's
/// language shows it and as the site stores it.
/// </summary>
public static class ListLocator
{
    /// <summary>Site Pages library template. Its title is translated too ("Websiteseiten", "Pages du site").</summary>
    public const int SitePagesTemplate = 119;

    /// <summary>The list, or an exception naming what was looked for. The returned object is unloaded; Load what you need.</summary>
    public static async Task<List> FindAsync(ClientContext ctx, string? title, string? serverRelativeUrl = null) =>
        await TryFindAsync(ctx, title, serverRelativeUrl)
        ?? throw new InvalidOperationException(
            $"List '{title}'" + (string.IsNullOrEmpty(serverRelativeUrl) ? "" : $" ({serverRelativeUrl})") +
            $" was not found at {ctx.Url}. It may have been renamed, moved or deleted.");

    /// <summary>The list, or null when neither the path nor the title matches anything on this web.</summary>
    public static async Task<List?> TryFindAsync(ClientContext ctx, string? title, string? serverRelativeUrl = null)
    {
        var id = await TryFindIdAsync(ctx, title, serverRelativeUrl);
        return id is null ? null : ctx.Web.Lists.GetById(id.Value);
    }

    public static async Task<Guid?> TryFindIdAsync(ClientContext ctx, string? title, string? serverRelativeUrl = null)
    {
        var lists = ctx.Web.Lists;
        ctx.Load(lists, ls => ls.Include(l => l.Id, l => l.Title, l => l.RootFolder.ServerRelativeUrl));
        await ctx.ExecuteWithRetryAsync();
        var all = lists.AsEnumerable().ToList();

        // 1. Root folder path: the same in every language.
        var path = NormalizePath(serverRelativeUrl);
        if (path.Length > 0)
        {
            var byPath = all.FirstOrDefault(l => NormalizePath(l.RootFolder.ServerRelativeUrl) == path);
            if (byPath != null) return byPath.Id;
        }

        if (string.IsNullOrWhiteSpace(title)) return null;
        title = title.Trim();

        // 2. Title as this connection's language shows it.
        var byTitle = all.Where(l => string.Equals(l.Title, title, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byTitle.Count == 1) return byTitle[0].Id;

        // 3. Title as the site stores it (its default language). Also the tie-breaker when a translated
        //    built-in title collides with a real list of the same name.
        Guid? stored = null;
        try
        {
            var list = ctx.Web.Lists.GetByTitle(title);
            ctx.Load(list, l => l.Id);
            await ctx.ExecuteWithRetryAsync();
            stored = list.Id;
        }
        catch (ServerException) { /* no list stored under this title */ }

        if (stored != null) return stored;
        return byTitle.Count > 0 ? byTitle[0].Id : null;
    }

    /// <summary>The first list of a template on this web, e.g. the Site Pages library, whatever it is called.</summary>
    public static async Task<List?> TryFindByTemplateAsync(ClientContext ctx, int baseTemplate)
    {
        var lists = ctx.Web.Lists;
        ctx.Load(lists, ls => ls.Include(l => l.Id, l => l.BaseTemplate));
        await ctx.ExecuteWithRetryAsync();
        var match = lists.AsEnumerable().FirstOrDefault(l => l.BaseTemplate == baseTemplate);
        return match is null ? null : ctx.Web.Lists.GetById(match.Id);
    }

    private static string NormalizePath(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        var path = url.Trim();
        if (Uri.TryCreate(path, UriKind.Absolute, out var abs) && abs.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            path = abs.AbsolutePath;
        try { path = Uri.UnescapeDataString(path); } catch (UriFormatException) { }
        return path.TrimEnd('/').ToLowerInvariant();
    }
}

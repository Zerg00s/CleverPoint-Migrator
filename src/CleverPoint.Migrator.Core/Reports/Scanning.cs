using System.Text.Json;
using CleverPoint.Migrator.Core.Csom;
using CleverPoint.Migrator.Core.Http;

namespace CleverPoint.Migrator.Core.Reports;

/// <summary>Reading whole collections from SharePoint REST, however many pages they take.</summary>
public static class RestPaging
{
    /// <summary>Calls <paramref name="onItem"/> for every item of a collection, following the server's "next page" links.</summary>
    public static async Task ForEachAsync(this SpRestClient rest, string url, Action<JsonElement> onItem, CancellationToken ct = default)
    {
        var next = url;
        while (next is not null)
        {
            ct.ThrowIfCancellationRequested();
            using var page = await rest.GetJsonAsync(next);
            var root = page.RootElement;
            if (root.TryGetProperty("value", out var items) && items.ValueKind == JsonValueKind.Array)
                foreach (var item in items.EnumerateArray()) onItem(item);
            next = root.TryGetProperty("odata.nextLink", out var link) && link.ValueKind == JsonValueKind.String ? link.GetString() : null;
        }
    }

    /// <summary>Every item of a collection, kept in memory. For collections that are small per site (lists, users, features).</summary>
    public static async Task<List<JsonElement>> GetAllAsync(this SpRestClient rest, string url, CancellationToken ct = default)
    {
        var all = new List<JsonElement>();
        await rest.ForEachAsync(url, item => all.Add(item.Clone()), ct);
        return all;
    }
}

/// <summary>
/// Sites change while a report reads them. A list that existed when the site's lists were read and is
/// gone a moment later is not a reason to give up on the site: the report skips it and counts it.
/// </summary>
public static class Gone
{
    /// <summary>The summary number reports add to when they skip something that was deleted under them.</summary>
    public const string Counter = "deletedDuringScan";

    /// <summary>True when the error says that what was being read no longer exists.</summary>
    public static bool Is(Exception ex) => ex switch
    {
        SpRestException rest => rest.StatusCode == 404,
        Microsoft.SharePoint.Client.ServerException server =>
            server.ServerErrorTypeName?.Contains("FileNotFound", StringComparison.OrdinalIgnoreCase) == true
            || server.Message.Contains("does not exist", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };
}

/// <summary>One web (a site's top level or a subsite).</summary>
public sealed record WebInfo(string Url, string Title, string ParentUrl, int Level, string Template, DateTime? Created, DateTime? LastModified, long Language);

/// <summary>Walks a site's subsites at every level.</summary>
public static class WebWalker
{
    private const string Select = "$select=Title,Url,WebTemplate,Configuration,Created,LastItemModifiedDate,Language";

    /// <summary>Every subsite below a site, parents before children.</summary>
    public static async Task<List<WebInfo>> SubsitesAsync(SpConnection connection, CancellationToken ct)
    {
        var found = new List<WebInfo>();
        var queue = new Queue<(string Url, int Level)>();
        queue.Enqueue((connection.SiteUrl, 0));
        while (queue.Count > 0)
        {
            var (parent, level) = queue.Dequeue();
            List<JsonElement> children;
            try
            {
                children = await connection.Rest.GetAllAsync($"{parent}/_api/web/webs?{Select}&$top=500", ct);
            }
            catch (SpRestException) when (level > 0)
            {
                // A subsite the account cannot open: its own children stay unknown, the rest is still worth having.
                continue;
            }
            foreach (var child in children)
            {
                var url = child.Str("Url").TrimEnd('/');
                if (url.Length == 0) continue;
                var template = child.Str("WebTemplate");
                var configuration = child.Str("Configuration");
                found.Add(new WebInfo(url, child.Str("Title"), parent, level + 1,
                    configuration.Length > 0 ? $"{template}#{configuration}" : template,
                    child.When("Created"), child.When("LastItemModifiedDate"), child.Num("Language")));
                queue.Enqueue((url, level + 1));
            }
        }
        return found;
    }

    /// <summary>The site itself and, when asked, every subsite: the webs a per-list report walks.</summary>
    public static async Task<List<string>> WebsAsync(ReportContext context, bool includeSubsites)
    {
        var webs = new List<string> { context.Connection.SiteUrl };
        if (includeSubsites) webs.AddRange((await SubsitesAsync(context.Connection, context.Cancel)).Select(w => w.Url));
        return webs;
    }
}

public static class ListTypes
{
    /// <summary>What a list is, from its template number, in the words the site uses.</summary>
    public static string Name(long baseTemplate) => baseTemplate switch
    {
        100 => "Custom list",
        101 => "Document library",
        102 => "Survey",
        103 => "Links",
        104 => "Announcements",
        105 => "Contacts",
        106 => "Calendar",
        107 => "Tasks",
        108 => "Discussion board",
        109 => "Picture library",
        110 => "Data sources",
        115 => "Form library",
        116 => "Master page gallery",
        119 => "Site pages",
        120 => "Custom list (datasheet)",
        121 => "Solutions gallery",
        122 => "Theme gallery",
        130 => "Data connection library",
        140 => "Workflow history",
        150 => "Project tasks",
        170 => "Promoted links",
        171 => "Tasks (timeline)",
        175 => "Maintenance logs",
        200 => "Meeting series",
        544 => "Persistent storage",
        700 => "My sites documents",
        850 => "Pages library",
        851 => "Asset library",
        1100 => "Issue tracking",
        3100 => "Access requests",
        _ => $"Template {baseTemplate}",
    };

    /// <summary>The site-relative address of a list from its root folder, as a full URL.</summary>
    public static string Url(string webUrl, string serverRelativeUrl)
    {
        if (serverRelativeUrl.Length == 0) return "";
        var authority = new Uri(webUrl).GetLeftPart(UriPartial.Authority);
        return authority + serverRelativeUrl;
    }
}

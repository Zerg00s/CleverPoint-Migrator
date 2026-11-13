using System.Text.Json;
using CleverPoint.Migrator.Core.Csom;
using CleverPoint.Migrator.Core.Hosting;
using Microsoft.Online.SharePoint.TenantAdministration;

namespace CleverPoint.Migrator.Core.Reports;

/// <summary>
/// Lists a tenant's site collections and OneDrives through its admin site. Needs a connection that
/// SharePoint accepts as a tenant administrator: an app with Sites.FullControl.All, or a person who
/// is a SharePoint administrator.
///
/// Site collections come from the admin site's own list of sites, which carries what the admin
/// center shows (hub, group, activity, file count). OneDrives are not in that list; they come from
/// the tenant API.
/// </summary>
public static class TenantSites
{
    /// <summary>The list behind the admin center's "Active sites" page.</summary>
    private const string AggregatedList = "DO_NOT_DELETE_SPLIST_TENANTADMIN_AGGREGATED_SITECOLLECTIONS";

    /// <summary>Every site collection (not OneDrives), as the raw list items, in pages.</summary>
    public static async Task ForEachSiteAsync(SpConnection admin, Action<JsonElement> onSite, Action<string>? progress, CancellationToken ct)
    {
        // Paged by item ID: it stays correct past the 5,000-item view threshold and when sites are
        // created or deleted while the listing runs.
        var lastId = 0L;
        var total = 0;
        const int pageSize = 2000;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var url = $"{admin.SiteUrl}/_api/web/lists/GetByTitle('{AggregatedList}')/items?$top={pageSize}&$orderby=ID asc&$filter=ID gt {lastId}";
            using var page = await admin.Rest.GetJsonAsync(url);
            if (!page.RootElement.TryGetProperty("value", out var items) || items.GetArrayLength() == 0) break;
            foreach (var item in items.EnumerateArray())
            {
                var id = item.Num("ID");
                if (id == 0) id = item.Num("Id");
                if (id > lastId) lastId = id;
                if (item.Str("SiteUrl").Length == 0) continue;
                onSite(item);
                total++;
            }
            progress?.Invoke($"{total} sites listed");
            if (items.GetArrayLength() < pageSize) break;
        }
    }

    /// <summary>Every OneDrive of the tenant, in pages.</summary>
    public static async Task ForEachOneDriveAsync(SpConnection admin, Action<SiteProperties> onSite, Action<string>? progress, CancellationToken ct)
    {
        using var context = admin.CreateContext();
        var tenant = new Tenant(context);
        var filter = new SPOSitePropertiesEnumerableFilter
        {
            IncludePersonalSite = PersonalSiteFilter.Include,
            IncludeDetail = true,
            Template = "SPSPERS",
            StartIndex = null,
        };
        var total = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = tenant.GetSitePropertiesFromSharePointByFilters(filter);
            context.Load(page);
            await context.ExecuteWithRetryAsync();
            foreach (var site in page)
            {
                if (!site.Url.Contains("/personal/", StringComparison.OrdinalIgnoreCase)) continue;
                onSite(site);
                total++;
            }
            progress?.Invoke($"{total} OneDrives listed");
            if (string.IsNullOrEmpty(page.NextStartIndexFromSharePoint)) break;
            filter.StartIndex = page.NextStartIndexFromSharePoint;
        }
    }

    /// <summary>
    /// The lock state of every site collection (Unlock, ReadOnly, NoAccess), by URL. The admin site's
    /// list of sites does not carry it, so it is read from the tenant API.
    /// </summary>
    public static async Task<Dictionary<string, string>> LockStatesAsync(SpConnection admin, Action<string>? progress, CancellationToken ct)
    {
        var states = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var context = admin.CreateContext();
        var tenant = new Tenant(context);
        var filter = new SPOSitePropertiesEnumerableFilter { IncludePersonalSite = PersonalSiteFilter.Exclude, IncludeDetail = false, StartIndex = null };
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = tenant.GetSitePropertiesFromSharePointByFilters(filter);
            context.Load(page);
            await context.ExecuteWithRetryAsync();
            foreach (var site in page) states[site.Url.TrimEnd('/')] = site.LockState ?? "";
            progress?.Invoke($"{states.Count} lock states read");
            if (string.IsNullOrEmpty(page.NextStartIndexFromSharePoint)) break;
            filter.StartIndex = page.NextStartIndexFromSharePoint;
        }
        return states;
    }

    /// <summary>A lock state in words.</summary>
    public static string LockStateName(string state) => state.ToLowerInvariant() switch
    {
        "unlock" => "Unlocked",
        "readonly" => "Read-only",
        "noaccess" => "No access",
        "" => "",
        _ => state,
    };

    /// <summary>The URLs of a tenant's sites, for "all sites" runs.</summary>
    /// <param name="type">sharepoint, onedrive or all.</param>
    /// <returns>
    /// The URLs, and for the sites among them that nobody can open right now (locked with no access,
    /// archived) the reason, so a run can say that instead of a bare "access denied".
    /// </returns>
    public static async Task<(List<string> Urls, Dictionary<string, (string Status, string Reason)> Closed)> ListUrlsAsync(
        SpConnection admin, string type, string? urlContains, string? template, Action<string>? progress, CancellationToken ct)
    {
        var urls = new List<string>();
        var closed = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        var kind = type.ToLowerInvariant();
        bool Wanted(string url, string siteTemplate, bool deleted) =>
            !deleted
            && (string.IsNullOrWhiteSpace(urlContains) || url.Contains(urlContains, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(template) || siteTemplate.Equals(template, StringComparison.OrdinalIgnoreCase));

        if (kind is "sharepoint" or "all")
        {
            Dictionary<string, string> locks;
            try { locks = await LockStatesAsync(admin, progress, ct); }
            catch (Microsoft.SharePoint.Client.ServerException) { locks = new(); }      // the listing still works without them
            await ForEachSiteAsync(admin, item =>
            {
                var url = item.Str("SiteUrl");
                var siteTemplate = item.Str("TemplateName");
                // The list also holds the system sites nobody means by "all sites".
                if (IsSystemSite(url, siteTemplate) || IsOneDrive(url, siteTemplate)) return;
                if (!Wanted(url, siteTemplate, item.When("TimeDeleted") is not null)) return;
                urls.Add(url);
                var key = JobSpecs.NormalizeSite(url);
                if (item.Str("ArchiveStatus") is { Length: > 0 } archive && !archive.Equals("None", StringComparison.OrdinalIgnoreCase))
                    closed[key] = ("archived", "The site is archived. Reactivate it to scan it.");
                else if (locks.GetValueOrDefault(url.TrimEnd('/'))?.Equals("NoAccess", StringComparison.OrdinalIgnoreCase) == true)
                    closed[key] = ("locked", "The site is locked with no access. Unlock it or set it to read-only to scan it.");
            }, progress, ct);
        }

        if (kind is "onedrive" or "all")
            await ForEachOneDriveAsync(admin, site =>
            {
                if (Wanted(site.Url, site.Template ?? "", deleted: false)) urls.Add(site.Url);
            }, progress, ct);

        return (ReportSpecs.Distinct(urls), closed);
    }

    public static bool IsOneDrive(string url, string template) =>
        template.Contains("SPSPERS", StringComparison.OrdinalIgnoreCase) || url.Contains("-my.sharepoint.com/personal", StringComparison.OrdinalIgnoreCase);

    /// <summary>Sites SharePoint keeps for itself: search center, app catalog, the OneDrive host, redirect sites and the like.</summary>
    public static bool IsSystemSite(string url, string template)
    {
        var t = template.ToUpperInvariant();
        return t.StartsWith("SRCHCEN") || t.StartsWith("SPSMSITEHOST") || t.StartsWith("APPCATALOG") || t.StartsWith("REDIRECTSITE")
               || t.StartsWith("TENANTADMIN") || t.StartsWith("POINTPUBLISHING")
               || url.Contains("-my.sharepoint.com", StringComparison.OrdinalIgnoreCase) && !url.Contains("/personal/", StringComparison.OrdinalIgnoreCase)
               || url.Contains("-admin.sharepoint.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Team site, communication site, OneDrive: the broad kind, from the template and address.</summary>
    public static string SiteType(string url, string template)
    {
        if (IsOneDrive(url, template)) return "OneDrive";
        if (template.Equals("SITEPAGEPUBLISHING#0", StringComparison.OrdinalIgnoreCase)) return "Communication site";
        if (template.StartsWith("TEAMCHANNEL", StringComparison.OrdinalIgnoreCase)) return "Channel site";
        if (template.Contains("GROUP", StringComparison.OrdinalIgnoreCase) || template.Equals("STS#3", StringComparison.OrdinalIgnoreCase)
            || template.Equals("STS#0", StringComparison.OrdinalIgnoreCase)) return "Team site";
        return "SharePoint site";
    }

    public static string TemplateName(string template) => template.ToUpperInvariant() switch
    {
        "" => "",
        "GROUP#0" => "Team site (Microsoft 365 group)",
        "STS#3" => "Team site (no group)",
        "STS#0" => "Classic team site",
        "STS#1" => "Blank site",
        "STS#2" => "Document workspace",
        "SITEPAGEPUBLISHING#0" => "Communication site",
        "TEAMCHANNEL#0" => "Private channel site",
        "TEAMCHANNEL#1" => "Shared channel site",
        "APPCATALOG#0" => "App catalog",
        "BDR#0" => "Document center",
        "BICENTERSITE#0" => "Business intelligence center",
        "BLANKINTERNET#0" or "CMSPUBLISHING#0" => "Publishing site",
        "BLANKINTERNETCONTAINER#0" => "Publishing portal",
        "BLOG#0" => "Blog",
        "COMMUNITY#0" => "Community site",
        "COMMUNITYPORTAL#0" => "Community portal",
        "DEV#0" => "Developer site",
        "EDISC#0" => "eDiscovery center",
        "EDISC#1" => "eDiscovery case",
        "EHS#1" => "Team site (legacy root)",
        "ENTERWIKI#0" => "Enterprise wiki",
        "OFFILE#1" => "Records center",
        "POINTPUBLISHINGHUB#0" => "Video portal hub",
        "POINTPUBLISHINGTOPIC#0" => "Video channel",
        "PROJECTSITE#0" => "Project site",
        "PWA#0" => "Project web app",
        "REDIRECTSITE#0" or "REDIRECTSITE#1" => "Redirect site",
        "SPSMSITEHOST#0" => "OneDrive host",
        "SRCHCEN#0" => "Enterprise search center",
        "SRCHCENTERLITE#0" or "SRCHCENTERLITE#1" => "Basic search center",
        "WIKI#0" => "Wiki site",
        var t when t.StartsWith("SPSPERS") => "OneDrive",
        _ => template,
    };

    public static string Language(long lcid) => lcid switch
    {
        0 => "",
        1025 => "Arabic", 1026 => "Bulgarian", 1027 => "Catalan", 1028 => "Chinese (Traditional)", 1029 => "Czech",
        1030 => "Danish", 1031 => "German", 1032 => "Greek", 1033 => "English", 1035 => "Finnish", 1036 => "French",
        1037 => "Hebrew", 1038 => "Hungarian", 1040 => "Italian", 1041 => "Japanese", 1042 => "Korean", 1043 => "Dutch",
        1044 => "Norwegian", 1045 => "Polish", 1046 => "Portuguese (Brazil)", 1048 => "Romanian", 1049 => "Russian",
        1050 => "Croatian", 1051 => "Slovak", 1053 => "Swedish", 1054 => "Thai", 1055 => "Turkish", 1057 => "Indonesian",
        1058 => "Ukrainian", 1060 => "Slovenian", 1061 => "Estonian", 1062 => "Latvian", 1063 => "Lithuanian",
        1066 => "Vietnamese", 1081 => "Hindi", 1086 => "Malay", 2052 => "Chinese (Simplified)", 2070 => "Portuguese (Portugal)",
        2074 => "Serbian (Latin)", 3082 => "Spanish",
        _ => lcid.ToString(),
    };
}

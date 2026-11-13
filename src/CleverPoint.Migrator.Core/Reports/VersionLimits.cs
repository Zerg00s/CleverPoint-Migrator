using System.Collections.Concurrent;
using System.Text.Json;
using CleverPoint.Migrator.Core.Csom;
using CleverPoint.Migrator.Core.Http;

namespace CleverPoint.Migrator.Core.Reports;

/// <summary>
/// Version history limits: how many versions of a file SharePoint keeps, and for how long. There are
/// three places that say so. The tenant has a default. A site can have its own setting, which only
/// decides what new libraries get. And every library has the limits it actually works by, which is
/// what counts for storage: a library made before the tenant switched to automatic limits still
/// keeps its 500 versions forever until someone changes it.
/// </summary>
public static class VersionLimits
{
    public const string Automatic = "Automatic";
    public const string TenantDefault = "Tenant default";
    public const string NoVersioning = "No versioning";
    private const string Forever = "kept forever";

    /// <summary>One document library and the limits it works by.</summary>
    public sealed record Library(string Id, string Title, string Url, bool Hidden, long Items, bool Versioning, long TrimMode, long MajorVersions, long ExpireAfterDays)
    {
        public string Limits => Describe(Versioning, TrimMode, MajorVersions, ExpireAfterDays);
    }

    /// <summary>Limits in words. <paramref name="trimMode"/> is SharePoint's: 0 no expiry, 1 expire after days, 2 automatic.</summary>
    public static string Describe(bool versioning, long trimMode, long majorVersions, long expireAfterDays)
    {
        if (!versioning) return NoVersioning;
        return trimMode switch
        {
            2 => Automatic,
            1 => Describe(false, majorVersions, expireAfterDays),
            0 => Describe(false, majorVersions, 0),
            _ => $"{majorVersions:N0} versions, other rules",
        };
    }

    public static string Describe(bool automatic, long majorVersions, long expireAfterDays)
    {
        if (automatic) return Automatic;
        // A limit of 0 is how SharePoint says there is none. Site Pages libraries are made that way: every version is kept.
        var count = majorVersions > 0 ? $"{majorVersions:N0} versions" : "Every version";
        return expireAfterDays > 0 ? $"{count}, deleted after {expireAfterDays:N0} days" : $"{count}, {Forever}";
    }

    /// <summary>Whether limits put this way never delete a version because of its age.</summary>
    public static bool KeepsForever(string limits) => limits.EndsWith(Forever, StringComparison.Ordinal);

    /// <summary>The document libraries of a web with their limits. Empty when the web does not answer the question.</summary>
    public static async Task<List<Library>> LibrariesAsync(SpConnection connection, string webUrl, CancellationToken ct)
    {
        var authority = new Uri(webUrl).GetLeftPart(UriPartial.Authority);
        try
        {
            var lists = await connection.Rest.GetAllAsync(
                $"{webUrl}/_api/web/lists?$select=Id,Title,Hidden,ItemCount,EnableVersioning,MajorVersionLimit,VersionPolicies/DefaultTrimMode,VersionPolicies/DefaultExpireAfterDays," +
                "RootFolder/ServerRelativeUrl&$expand=RootFolder,VersionPolicies&$filter=BaseType eq 1&$top=500", ct);
            return lists.Select(l => new Library(l.Str("Id"), l.Str("Title"), authority + Links.Escape(l.Child("RootFolder").Str("ServerRelativeUrl")), l.Bool("Hidden"), l.Num("ItemCount"),
                l.Bool("EnableVersioning"), l.Child("VersionPolicies").Num("DefaultTrimMode"), l.Num("MajorVersionLimit"), l.Child("VersionPolicies").Num("DefaultExpireAfterDays"))).ToList();
        }
        catch (SpRestException)
        {
            return new List<Library>();
        }
    }

    /// <summary>
    /// A site's own setting, which is what its new libraries get: "Tenant default" when the site has
    /// none. Empty when it cannot be read.
    /// </summary>
    public static async Task<string> SiteAsync(SpConnection connection, string siteUrl)
    {
        try
        {
            using var doc = await connection.Rest.GetJsonAsync($"{siteUrl}/_api/site/VersionPolicyForNewLibrariesTemplate?$expand=VersionPolicies");
            var template = doc.RootElement;
            // A site without a setting of its own answers with a limit of -1.
            if (template.Num("MajorVersionLimit") < 0) return TenantDefault;
            var policies = template.Child("VersionPolicies");
            return Describe(true, policies.Num("DefaultTrimMode"), template.Num("MajorVersionLimit"), policies.Num("DefaultExpireAfterDays"));
        }
        catch (SpRestException)
        {
            return "";
        }
    }

    private static readonly ConcurrentDictionary<string, (DateTime Read, Task<string?> Limits)> Tenants = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The tenant's default, read from its admin site. Null when no connection can read that site,
    /// which is the usual case for someone who is not a SharePoint administrator. Asked once per
    /// tenant and remembered for a few minutes, so a report over hundreds of sites asks once.
    /// </summary>
    public static Task<string?> TenantAsync(Func<string, Task<SpConnection>> connect, string siteUrl)
    {
        var admin = ReportSpecs.AdminUrl(siteUrl);
        var known = Tenants.GetOrAdd(admin, _ => (DateTime.UtcNow, ReadTenantAsync(connect, admin)));
        if (DateTime.UtcNow - known.Read > TimeSpan.FromMinutes(5))
            Tenants[admin] = known = (DateTime.UtcNow, ReadTenantAsync(connect, admin));
        return known.Limits;
    }

    private static async Task<string?> ReadTenantAsync(Func<string, Task<SpConnection>> connect, string admin)
    {
        try
        {
            var connection = await connect(admin);
            using var doc = await connection.Rest.GetJsonAsync($"{admin}/_api/SPO.Tenant?$select=EnableAutoExpirationVersionTrim,ExpireVersionsAfterDays,MajorVersionLimit");
            var tenant = doc.RootElement;
            return Describe(tenant.Bool("EnableAutoExpirationVersionTrim"), tenant.Num("MajorVersionLimit"), tenant.Num("ExpireVersionsAfterDays"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// What SharePoint says about a job it runs in the background, as it comes back from the admin
    /// site: a piece of JSON, sometimes wrapped in a string. Null when there is no such job.
    /// </summary>
    public static JsonElement? Job(string? progress)
    {
        if (string.IsNullOrWhiteSpace(progress)) return null;
        try
        {
            var text = progress.TrimStart().StartsWith('"') ? JsonSerializer.Deserialize<string>(progress) ?? "" : progress;
            using var doc = JsonDocument.Parse(text);
            var job = doc.RootElement.Clone();
            var status = job.Str("Status");
            return status.Length == 0 || status.Equals("NoRequestFound", StringComparison.OrdinalIgnoreCase) ? null : job;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A job's status in words: "In progress", "Completed with errors".</summary>
    public static string JobStatus(JsonElement job) => ReportCatalog.Heading(job.Str("Status"));
}

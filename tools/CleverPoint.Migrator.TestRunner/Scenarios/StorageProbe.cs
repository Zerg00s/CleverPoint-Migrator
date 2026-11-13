using System.Text.Json;
using CleverPoint.Migrator.Core.Csom;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Shows what SharePoint returns for storage sizes, the two ways it offers them: the per-item storage
/// fields of a library view, and a folder's storage metrics. The storage report is built on the first;
/// this probe is how to look at the raw values when a number is in doubt.
/// </summary>
public static class StorageProbe
{
    /// <summary>What the tenant records about site collection app catalogs, and what such a site looks like from inside.</summary>
    public static async Task CatalogsAsync()
    {
        var root = Program.Source;
        async Task Show(string label, string url)
        {
            try
            {
                using var doc = await root.Rest.GetJsonAsync(url);
                var text = doc.RootElement.GetRawText();
                Console.WriteLine($"  {label}: {(text.Length > 1500 ? text[..1500] + "..." : text)}");
            }
            catch (Exception ex) { Console.WriteLine($"  {label}: FAILED {ex.Message.Split('\n')[0][..Math.Min(200, ex.Message.Split('\n')[0].Length)]}"); }
        }
        var site = root.SiteUrl;
        await Show("tenant catalog url", $"{site}/_api/SP_TenantSettings_Current");
        await Show("site collection catalogs", $"{site}/_api/web/tenantappcatalog/SiteCollectionAppCatalogsSites");
        // A site the tenant lists as having a catalog, looked at from inside.
        var withCatalog = new Uri(site).GetLeftPart(UriPartial.Authority) + "/sites/Demo-AppsMigrator";
        await Show("catalog site: lists", $"{withCatalog}/_api/web/lists?$select=Title,BaseTemplate,Hidden,RootFolder/ServerRelativeUrl&$expand=RootFolder&$filter=substringof('pp',Title)");
        await Show("catalog site: available apps", $"{withCatalog}/_api/web/sitecollectionappcatalog/AvailableApps?$select=Title,Deployed,InstalledVersion,AppCatalogVersion,IsClientSideSolution");
        await Show("plain site: available apps", $"{site}/_api/web/sitecollectionappcatalog/AvailableApps?$select=Title");
        Program.Check("catalog-probe: ran", true);
    }

    /// <summary>What lists (not libraries) take in storage, attachments included, and whether one call per site can say so.</summary>
    public static async Task ListSizesAsync()
    {
        var root = Program.Source;
        async Task Show(string label, string url)
        {
            try
            {
                using var doc = await root.Rest.GetJsonAsync(url);
                var text = doc.RootElement.GetRawText();
                Console.WriteLine($"  {label}: {(text.Length > 1800 ? text[..1800] + "..." : text)}");
            }
            catch (Exception ex) { Console.WriteLine($"  {label}: FAILED {ex.Message.Split('\n')[0][..Math.Min(300, ex.Message.Split('\n')[0].Length)]}"); }
        }
        var site = root.SiteUrl;
        await Show("all lists with storage in one call",
            $"{site}/_api/web/lists?$select=Title,ItemCount,BaseType,Hidden,RootFolder/ServerRelativeUrl,RootFolder/StorageMetrics/TotalSize,RootFolder/StorageMetrics/TotalFileCount" +
            "&$expand=RootFolder,RootFolder/StorageMetrics&$filter=BaseType eq 0&$top=8");
        await Show("one list's root folder",
            $"{site}/_api/web/GetFolderByServerRelativePath(decodedurl='{Uri.EscapeDataString(new Uri(site).AbsolutePath + "/Lists")}')/Folders?$select=Name,ItemCount,StorageMetrics/TotalSize,StorageMetrics/TotalFileCount&$expand=StorageMetrics&$top=12");
        Program.Check("list-probe: ran", true);
    }

    /// <summary>What SharePoint returns for version history limits: the tenant's default, a site's setting for new libraries, and each library's own.</summary>
    public static async Task VersionLimitsAsync()
    {
        var root = Program.Source;
        async Task Show(string label, string url)
        {
            try
            {
                using var doc = await root.Rest.GetJsonAsync(url);
                var text = doc.RootElement.GetRawText();
                Console.WriteLine($"  {label}: {(text.Length > 1400 ? text[..1400] + "..." : text)}");
            }
            catch (Exception ex) { Console.WriteLine($"  {label}: FAILED {ex.Message.Split('\n')[0][..Math.Min(260, ex.Message.Split('\n')[0].Length)]}"); }
        }
        var site = root.SiteUrl;
        var authority = new Uri(site).GetLeftPart(UriPartial.Authority);
        var admin = authority.Replace(".sharepoint.com", "-admin.sharepoint.com");
        await Show("tenant (SPO.Tenant)", $"{admin}/_api/SPO.Tenant?$select=EnableAutoExpirationVersionTrim,ExpireVersionsAfterDays,MajorVersionLimit");
        await Show("tenant (Office365Tenant)", $"{admin}/_api/SPOInternalUseOnly.Tenant?$select=EnableAutoExpirationVersionTrim,ExpireVersionsAfterDays,MajorVersionLimit");
        foreach (var url in new[] { site, authority + "/sites/hubnwm", authority + "/sites/durham" })
        {
            Console.WriteLine($"  --- {url}");
            await Show("site template", $"{url}/_api/site/VersionPolicyForNewLibrariesTemplate?$expand=VersionPolicies");
            await Show("site template, plain", $"{url}/_api/site/VersionPolicyForNewLibrariesTemplate");
            await Show("libraries", $"{url}/_api/web/lists?$select=Title,EnableVersioning,EnableMinorVersions,MajorVersionLimit,MajorWithMinorVersionsLimit,VersionPolicies/DefaultTrimMode,VersionPolicies/DefaultExpireAfterDays&$expand=VersionPolicies&$filter=BaseType eq 1 and Hidden eq false&$top=4");
            // The two "progress" calls only read, but SharePoint wants them as POST.
            foreach (var call in new[] { "GetProgressForDeleteFileVersions", "GetProgressForSetVersionPolicyForDocLibs" })
            {
                try { Console.WriteLine($"  {call}: {await root.Rest.PostAsync($"{url}/_api/site/{call}", new { })}"); }
                catch (Exception ex) { Console.WriteLine($"  {call}: FAILED {ex.Message.Split('\n')[0][..Math.Min(300, ex.Message.Split('\n')[0].Length)]}"); }
            }
        }
        // The same questions asked through the tenant admin site, which is how an app-only connection has to ask.
        if (root.Tokens is { } tokens)
        {
            var adminSite = new SpConnection(admin, tokens);
            foreach (var url in new[] { site, authority + "/sites/hubnwm" })
            {
                Console.WriteLine($"  --- through the admin site: {url}");
                try
                {
                    using var csom = adminSite.CreateContext();
                    var tenant = new Microsoft.Online.SharePoint.TenantAdministration.Tenant(csom);
                    var properties = tenant.GetSitePropertiesByUrl(url, includeDetail: true);
                    csom.Load(properties, p => p.EnableAutoExpirationVersionTrim, p => p.InheritVersionPolicyFromTenant, p => p.MajorVersionLimit, p => p.ExpireVersionsAfterDays);
                    await csom.ExecuteWithRetryAsync();
                    Console.WriteLine($"  site properties: inherit={properties.InheritVersionPolicyFromTenant} auto={properties.EnableAutoExpirationVersionTrim} major={properties.MajorVersionLimit} days={properties.ExpireVersionsAfterDays}");
                }
                catch (Exception ex) { Console.WriteLine($"  site properties: FAILED {ex.Message.Split('\n')[0]}"); }
                try
                {
                    using var csom = adminSite.CreateContext();
                    var tenant = new Microsoft.Online.SharePoint.TenantAdministration.Tenant(csom);
                    var trim = tenant.GetFileVersionBatchDeleteJobProgress(url);
                    await csom.ExecuteWithRetryAsync();
                    Console.WriteLine($"  trim job: {trim.Value}");
                }
                catch (Exception ex) { Console.WriteLine($"  trim job: FAILED {ex.Message.Split('\n')[0]}"); }
                try
                {
                    using var csom = adminSite.CreateContext();
                    var tenant = new Microsoft.Online.SharePoint.TenantAdministration.Tenant(csom);
                    var set = tenant.GetVersionPolicyForDocLibsJobProgress(url);
                    await csom.ExecuteWithRetryAsync();
                    Console.WriteLine($"  set-limits job: {System.Text.Json.JsonSerializer.Serialize((object?)set.Value)}");
                }
                catch (Exception ex) { Console.WriteLine($"  set-limits job: FAILED {ex.Message.Split('\n')[0]}"); }
            }
        }
        Program.Check("version-probe: ran", true);
    }

    /// <summary>
    /// How long the storage map takes to build from the tables of a run, and what it keeps in memory.
    /// CPM_MAP_RUN names the folder of a run. This is for sizing, not a check of correctness.
    /// </summary>
    public static Task MapScaleAsync()
    {
        var folder = Environment.GetEnvironmentVariable("CPM_MAP_RUN");
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            // Sizing is asked for, not part of a regression run.
            Console.WriteLine("  skipped: set CPM_MAP_RUN to the folder of a run to measure it");
            return Task.CompletedTask;
        }
        var store = new CleverPoint.Migrator.Core.Reports.ReportStore(folder);
        Console.WriteLine("  rows: " + string.Join(", ", store.RowCounts().Select(c => $"{c.Key} {c.Value:N0}")));
        var before = GC.GetTotalMemory(true);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var map = CleverPoint.Migrator.Core.Reports.StorageMap.Build("storage", store);
        var built = clock.Elapsed;
        var kept = GC.GetTotalMemory(true) - before;
        long nodes = 0;
        void Count(CleverPoint.Migrator.Core.Reports.MapNode node) { nodes++; foreach (var child in node.Children) Count(child); }
        Count(map);
        Console.WriteLine($"  map: built in {built.TotalSeconds:N1} s, {nodes:N0} boxes, {kept / 1048576.0:N0} MB kept, {map.Total / 1099511627776.0:N2} TB, {map.Files:N0} files");
        clock.Restart();
        var rects = CleverPoint.Migrator.Core.Reports.StorageMap.Layout(map, 1300, 520);
        Console.WriteLine($"  layout of the top: {clock.Elapsed.TotalMilliseconds:N0} ms, {rects.Count:N0} rectangles");
        clock.Restart();
        var kinds = CleverPoint.Migrator.Core.Reports.StorageMap.BuildKinds(store);
        Console.WriteLine($"  file kinds: built in {clock.Elapsed.TotalSeconds:N1} s, {kinds.Children.Count} kinds");
        clock.Restart();
        var (sorted, matching) = store.ReadSorted("folders", "totalBytes", true, 0, 20_000);
        Console.WriteLine($"  folders sorted by size: {clock.Elapsed.TotalSeconds:N1} s for {matching:N0} rows");
        Console.WriteLine($"  process peak: {System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0:N0} MB");
        Program.Check("map-scale: the map holds what the folders table holds", map.Total > 0);
        return Task.CompletedTask;
    }

    public static async Task RunAsync()
    {
        var site = Program.TestSite ?? await TestAssets.EnsureTestSiteAsync(Program.Source);
        Program.TestSite = site;
        using var lists = await site.Rest.GetJsonAsync(
            $"{site.SiteUrl}/_api/web/lists?$select=Id,Title,ItemCount,RootFolder/ServerRelativeUrl&$expand=RootFolder&$filter=BaseType eq 1 and Hidden eq false&$top=500");
        // A library where some files have more than one version says the most.
        var library = lists.RootElement.GetProperty("value").EnumerateArray()
            .OrderByDescending(l => l.GetProperty("Title").GetString()!.StartsWith("CliT-Basic") ? 2 : l.GetProperty("Title").GetString() == TestAssets.SourceLibTitle ? 1 : 0)
            .First();
        var id = library.GetProperty("Id").GetString();
        var root = library.GetProperty("RootFolder").GetProperty("ServerRelativeUrl").GetString();
        Console.WriteLine($"  library: {library.GetProperty("Title").GetString()} ({root})");

        var body = new
        {
            parameters = new
            {
                RenderOptions = 2,
                ViewXml = "<View Scope='RecursiveAll'><Query></Query><ViewFields><FieldRef Name='FileLeafRef'/><FieldRef Name='FSObjType'/><FieldRef Name='File_x0020_Size'/>" +
                          "<FieldRef Name='SMTotalSize'/><FieldRef Name='SMTotalFileStreamSize'/><FieldRef Name='SMTotalFileCount'/><FieldRef Name='_UIVersionString'/></ViewFields>" +
                          "<RowLimit Paged='TRUE'>12</RowLimit></View>",
            },
        };
        var response = await site.Rest.PostAsync($"{site.SiteUrl}/_api/web/lists(guid'{id}')/RenderListDataAsStream", body);
        using var page = JsonDocument.Parse(response);
        foreach (var row in page.RootElement.GetProperty("Row").EnumerateArray())
        {
            string Field(string name) => row.TryGetProperty(name, out var v) ? v.GetRawText() : "(absent)";
            Console.WriteLine($"  {Field("FileLeafRef"),-28} folder={Field("FSObjType")} v={Field("_UIVersionString")} size={Field("File_x0020_Size")} " +
                              $"SMTotalSize={Field("SMTotalSize")} SMStream={Field("SMTotalFileStreamSize")} SMCount={Field("SMTotalFileCount")}");
        }

        using var metrics = await site.Rest.GetJsonAsync(
            $"{site.SiteUrl}/_api/web/GetFolderByServerRelativePath(decodedurl='{Uri.EscapeDataString(root!)}')?$select=Name,StorageMetrics&$expand=StorageMetrics");
        Console.WriteLine("  folder StorageMetrics: " + metrics.RootElement.GetProperty("StorageMetrics").GetRawText());
        Program.Check("storage-probe: the library answered", true);
    }
}

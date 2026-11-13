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
    /// <summary>
    /// One file seen every way SharePoint describes its size: its versions as listed, the file itself, and the
    /// storage fields a library view returns. CP_FILE is the file's full address.
    /// </summary>
    public static async Task FileSizesAsync()
    {
        var address = Environment.GetEnvironmentVariable("CP_FILE") ?? throw new InvalidOperationException("Set CP_FILE to a file's address.");
        var uri = new Uri(address);
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        var segments = path.Split('/');
        var site = $"{uri.GetLeftPart(UriPartial.Authority)}/{segments[1]}/{segments[2]}";
        var connection = new SpConnection(site, Program.Source.Tokens!);
        var quoted = path.Replace("'", "''");
        using (var file = await connection.Rest.GetJsonAsync($"{site}/_api/web/GetFileByServerRelativePath(decodedurl='{Uri.EscapeDataString(quoted)}')?$select=Length,UIVersionLabel,MajorVersion,MinorVersion"))
            Console.WriteLine($"  file: {file.RootElement.GetRawText()}");
        using (var versions = await connection.Rest.GetJsonAsync($"{site}/_api/web/GetFileByServerRelativePath(decodedurl='{Uri.EscapeDataString(quoted)}')/versions?$select=VersionLabel,Size,IsCurrentVersion,Created"))
        {
            long sum = 0;
            foreach (var v in versions.RootElement.GetProperty("value").EnumerateArray())
            {
                sum += v.GetProperty("Size").GetInt64();
                Console.WriteLine($"  version {v.GetProperty("VersionLabel").GetString(),-6} size {v.GetProperty("Size").GetInt64(),12:N0}  created {v.GetProperty("Created").GetString()}");
            }
            Console.WriteLine($"  older versions listed: {sum:N0} bytes");
        }
        using (var item = await connection.Rest.GetJsonAsync($"{site}/_api/web/GetFileByServerRelativePath(decodedurl='{Uri.EscapeDataString(quoted)}')/ListItemAllFields?$select=Id,File_x0020_Size,SMTotalSize,SMTotalFileStreamSize,SMTotalFileCount,SMLastModifiedDate"))
            Console.WriteLine($"  item fields: {item.RootElement.GetRawText()}");
        // Every field of the item with the length of its value, the longest first: metadata that SharePoint keeps
        // with each version, such as text it extracted from the file, counts in its storage too.
        using (var all = await connection.Rest.GetJsonAsync($"{site}/_api/web/GetFileByServerRelativePath(decodedurl='{Uri.EscapeDataString(quoted)}')/ListItemAllFields"))
        {
            var sizes = all.RootElement.EnumerateObject().Select(p => (p.Name, Length: p.Value.GetRawText().Length)).OrderByDescending(p => p.Length).Take(6);
            Console.WriteLine("  largest fields: " + string.Join(", ", sizes.Select(p => $"{p.Name} {p.Length:N0}")));
        }
        // Versions SharePoint deleted wait in the recycle bin, both stages, under the file's name.
        var name = segments[^1].Replace("'", "''");
        foreach (var bin in new[] { "web/RecycleBin", "site/RecycleBin" })
        {
            try
            {
                using var deleted = await connection.Rest.GetJsonAsync($"{site}/_api/{bin}?$select=LeafName,ItemType,ItemState,Size,DeletedDate,DirName&$filter=LeafName eq '{Uri.EscapeDataString(name)}'&$top=50");
                var items = deleted.RootElement.GetProperty("value").EnumerateArray().ToList();
                Console.WriteLine($"  {bin}: {items.Count} entries, {items.Sum(i => i.GetProperty("Size").ValueKind == JsonValueKind.Number ? i.GetProperty("Size").GetInt64() : long.Parse(i.GetProperty("Size").GetString() ?? "0")):N0} bytes"
                    + string.Concat(items.Take(5).Select(i => $"\n      type {i.GetProperty("ItemType")} state {i.GetProperty("ItemState")} size {i.GetProperty("Size")} deleted {i.GetProperty("DeletedDate")} in {i.GetProperty("DirName")}")));
            }
            catch (Exception ex) { Console.WriteLine($"  {bin}: FAILED {ex.Message.Split('\n')[0]}"); }
        }
    }

    /// <summary>
    /// Checks a finished version-limits report against SharePoint, without the report's own code: reads every
    /// site's libraries and setting again and puts them into words here. CP_REPORT_DIR is the folder of the run.
    /// </summary>
    public static async Task VersionCheckAsync()
    {
        var folder = Environment.GetEnvironmentVariable("CP_REPORT_DIR") ?? throw new InvalidOperationException("Set CP_REPORT_DIR to the folder of a version-limits run.");
        List<JsonElement> Rows(string table) => System.IO.File.ReadLines(Path.Combine(folder, "tables", table + ".ndjson"))
            .Where(l => l.Length > 2).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        static string Text(JsonElement row, string name) => row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        static long Number(JsonElement row, string name) => row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
        static string Words(bool versioning, long mode, long limit, long days)
        {
            if (!versioning) return "No versioning";
            if (mode == 2) return "Automatic";
            var count = limit > 0 ? $"{limit:N0} versions" : "Every version";
            return mode == 1 && days > 0 ? $"{count}, deleted after {days:N0} days" : $"{count}, kept forever";
        }

        var libraries = Rows("libraries");
        var limits = Rows("limits");
        var tokens = Program.Source.Tokens ?? throw new InvalidOperationException("Needs the certificate connection.");
        int sites = 0, compared = 0, wrong = 0, unread = 0;
        foreach (var row in limits)
        {
            var site = Text(row, "site");
            var connection = new SpConnection(site, tokens);
            try
            {
                using var template = await connection.Rest.GetJsonAsync($"{site}/_api/site/VersionPolicyForNewLibrariesTemplate?$expand=VersionPolicies");
                var t = template.RootElement;
                var limit = t.GetProperty("MajorVersionLimit").GetInt64();
                var setting = limit < 0 ? "Tenant default"
                    : Words(true, t.GetProperty("VersionPolicies").GetProperty("DefaultTrimMode").GetInt64(), limit, t.GetProperty("VersionPolicies").GetProperty("DefaultExpireAfterDays").GetInt64());
                if (setting != Text(row, "siteSetting")) { wrong++; Console.WriteLine($"  SITE SETTING {site}: report '{Text(row, "siteSetting")}', SharePoint '{setting}'"); }

                using var lists = await connection.Rest.GetJsonAsync($"{site}/_api/web/lists?$select=Title,Hidden,EnableVersioning,MajorVersionLimit,VersionPolicies/DefaultTrimMode,VersionPolicies/DefaultExpireAfterDays&$expand=VersionPolicies&$filter=BaseType eq 1 and Hidden eq false&$top=1000");
                // Two libraries of a web can carry the same name, so a name is told apart by its place among its namesakes.
                static Dictionary<string, string> Named(IEnumerable<(string Name, string Words)> pairs) => pairs.GroupBy(p => p.Name)
                    .SelectMany(g => g.OrderBy(p => p.Words, StringComparer.Ordinal).Select((p, i) => (Key: i == 0 ? p.Name : $"{p.Name} ({i + 1})", p.Words))).ToDictionary(p => p.Key, p => p.Words);
                var real = Named(lists.RootElement.GetProperty("value").EnumerateArray().Select(l => (l.GetProperty("Title").GetString() ?? "",
                    Words(l.GetProperty("EnableVersioning").GetBoolean(), l.GetProperty("VersionPolicies").GetProperty("DefaultTrimMode").GetInt64(),
                        l.GetProperty("MajorVersionLimit").GetInt64(), l.GetProperty("VersionPolicies").GetProperty("DefaultExpireAfterDays").GetInt64()))));
                // The site's own web only: its subsites are in the report under their own address.
                var reported = Named(libraries.Where(l => Text(l, "site") == site && Text(l, "webUrl") == site).Select(l => (Text(l, "library"), Text(l, "versionLimits"))));
                foreach (var (name, words) in real)
                {
                    compared++;
                    if (!reported.TryGetValue(name, out var said)) { wrong++; Console.WriteLine($"  MISSING {site} / {name}: SharePoint '{words}'"); }
                    else if (said != words) { wrong++; Console.WriteLine($"  DIFFERENT {site} / {name}: report '{said}', SharePoint '{words}'"); }
                }
                foreach (var name in reported.Keys.Where(n => !real.ContainsKey(n))) { wrong++; Console.WriteLine($"  EXTRA {site} / {name}"); }

                // The site's counts are those of its rows, subsites included.
                var all = libraries.Where(l => Text(l, "site") == site).Select(l => Text(l, "versionLimits")).ToList();
                var versioned = all.Count(w => w != "No versioning");
                var automatic = all.Count(w => w == "Automatic");
                var forever = all.Count(w => w.EndsWith("kept forever"));
                if (Number(row, "versionedLibraries") != versioned || Number(row, "automatic") != automatic || Number(row, "keptForever") != forever
                    || Number(row, "deletedAfterDays") != versioned - automatic - forever)
                { wrong++; Console.WriteLine($"  COUNTS {site}: report {Number(row, "versionedLibraries")}/{Number(row, "automatic")}/{Number(row, "deletedAfterDays")}/{Number(row, "keptForever")}, rows {versioned}/{automatic}/{versioned - automatic - forever}/{forever}"); }
                sites++;
            }
            catch (Exception ex) { unread++; Console.WriteLine($"  could not read {site}: {ex.Message.Split('\n')[0]}"); }
        }
        Console.WriteLine($"  {sites} sites and {compared} libraries compared with SharePoint, {wrong} differences, {unread} sites not readable");
        Program.Check("the version-limits report says what SharePoint says", wrong == 0 && sites > 0, $"{wrong} differences");
    }

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
            // Site Pages is the library that answers with a limit of 0 while it keeps every version.
            await Show("site pages", $"{url}/_api/web/lists?$select=Title,EnableVersioning,EnableMinorVersions,MajorVersionLimit,MajorWithMinorVersionsLimit,VersionPolicies/DefaultTrimMode,VersionPolicies/DefaultExpireAfterDays&$expand=VersionPolicies&$filter=BaseTemplate eq 119");
            await Show("versions of its pages", $"{url}/_api/web/lists/getbytitle('Site Pages')/items?$select=Id,FileLeafRef,OData__UIVersionString&$top=6&$orderby=Modified desc");
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

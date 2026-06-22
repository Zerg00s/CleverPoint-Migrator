using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Modern page migration: provision a client-side page with a text web part
/// (containing a link to the SOURCE site) on the migtest subsite, copy pages
/// cross-site to the parent web, verify the canvas arrived with URLs
/// rewritten to the target and the page published.
/// </summary>
public static class PageTests
{
    // Unique per run: this tenant's retention hold ghost-holds deleted page
    // names, silently auto-renaming any recreation (AddTemplateFile echoes
    // the requested URL, hiding the rename). Fresh names sidestep all of it.
    private static readonly string PageName = $"MigTest-Page-{DateTime.UtcNow:yyyyMMddHHmmss}.aspx";
    private static string ActualPageName = PageName;

    /// <summary>The pages API can lag a freshly created stub; retry briefly.</summary>
    private static async Task<int> GetPageIdWithRetryAsync(Core.Csom.SpConnection site, string webUrl, string name)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var doc = await site.Rest.GetJsonAsync(
                    $"{webUrl}/_api/sitepages/pages/GetByUrl(url='SitePages/{name}')");
                return doc.RootElement.GetProperty("Id").GetInt32();
            }
            catch (Core.Http.SpRestException ex) when (ex.StatusCode == 404 && attempt < 5)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
            }
        }
    }

    public static async Task RunAsync()
    {
        var site = Program.TestSite ?? await TestAssets.EnsureTestSiteAsync(Program.Source);
        Program.TestSite = site;

        // ---- Provision a modern page on the source subsite ----
        using (var ctx = site.CreateContext())
        {
            var pagesList = ctx.Web.Lists.GetByTitle("Site Pages");
            ctx.Load(pagesList.RootFolder, f => f.ServerRelativeUrl);
            ctx.Load(ctx.Web, w => w.Url);
            await ctx.ExecuteQueryAsync();
            var pageUrl = $"{pagesList.RootFolder.ServerRelativeUrl}/{PageName}";

            // Sweep this test's page AND any renamed duplicates from earlier
            // failed runs. Direct URL deletes also clear ORPHANED CHECKED-OUT
            // pages, which are invisible to item queries but hold the name.
            var sweep = pagesList.GetItems(CamlQuery.CreateAllItemsQuery(200));
            ctx.Load(sweep);
            await ctx.ExecuteQueryAsync();
            foreach (var stale in sweep.AsEnumerable()
                         .Where(i => ((string)i["FileRef"]).Split('/')[^1].StartsWith("MigTest-Page")).ToList())
            {
                stale.DeleteObject();
            }
            await ctx.ExecuteQueryAsync();
            Console.WriteLine($"  sweep {PageName}: {await PageCopier.TryDeleteFileAsync(ctx, pageUrl)}");
            for (var v = 1; v <= 5; v++)
            {
                var outcome = await PageCopier.TryDeleteFileAsync(ctx, pageUrl.Replace(".aspx", $"({v}).aspx"));
                if (outcome != "absent") Console.WriteLine($"  sweep ({v}): {outcome}");
            }

            var canvas = "[{\"position\":{\"controlIndex\":1,\"sectionIndex\":1,\"zoneIndex\":1,\"sectionFactor\":12,\"layoutIndex\":1}," +
                "\"controlType\":4,\"id\":\"a1b2c3d4-0000-4000-9000-000000000001\",\"editorType\":\"CKEditor\",\"addedFromPersistedData\":true," +
                $"\"innerHTML\":\"<p>Hello from the migrator test. Link: <a href=\\\"{ctx.Web.Url}/Shared%20Documents\\\">docs</a></p>\"}}]";

            var stub = ctx.Web.GetFolderByServerRelativeUrl(pagesList.RootFolder.ServerRelativeUrl)
                .Files.AddTemplateFile(pageUrl, TemplateFileType.ClientSidePage);
            ctx.Load(stub.ListItemAllFields, i => i.Id);
            ctx.Load(stub, f => f.ServerRelativeUrl);
            await ctx.ExecuteQueryAsync();
            // Retention-held ghosts can force an auto-rename; track reality.
            ActualPageName = stub.ServerRelativeUrl.Split('/')[^1];

            // Author the canvas through the SitePages REST API (the supported
            // surface; plain field writes go through the HTML sanitizer).
            // Pages are addressed by list item id: GetByUrl can't see stubs.
            var webUrl = ctx.Web.Url.TrimEnd('/');
            var pageId = stub.ListItemAllFields.Id;
            var payload = new Dictionary<string, object?>
            {
                ["__metadata"] = new Dictionary<string, string> { ["type"] = "SP.Publishing.SitePage" },
                // Title derives the file name on first save; keep them equal.
                ["Title"] = Path.GetFileNameWithoutExtension(PageName),
                ["CanvasContent1"] = canvas,
            };
            await site.Rest.PostAsync($"{webUrl}/_api/sitepages/pages({pageId})/checkoutpage");
            await site.Rest.PostRawAsync($"{webUrl}/_api/sitepages/pages({pageId})/SavePageAsDraft",
                System.Text.Json.JsonSerializer.Serialize(payload));
            await PageCopier.EnsureFileNameAsync(ctx, pagesList, pageId, pageUrl);
            await site.Rest.PostAsync($"{webUrl}/_api/sitepages/pages({pageId})/publish");

            using var probe = await site.Rest.GetJsonAsync($"{webUrl}/_api/sitepages/pages({pageId})");
            var probeCanvas = probe.RootElement.GetProperty("CanvasContent1").GetString() ?? "";
            Console.WriteLine($"  provisioned modern page {pageUrl} (source canvas: {probeCanvas.Length} chars)");
        }

        // ---- Clean any previous copies on the parent web (incl. renamed duplicates) ----
        using (var pctx = Program.Source.CreateContext())
        {
            var lib = pctx.Web.Lists.GetByTitle("Site Pages");
            var sweep = lib.GetItems(CamlQuery.CreateAllItemsQuery(200));
            pctx.Load(sweep);
            await pctx.ExecuteQueryAsync();
            foreach (var stale in sweep.AsEnumerable()
                         .Where(i => ((string)i["FileRef"]).Split('/')[^1].StartsWith("MigTest-Page")).ToList())
            {
                stale.DeleteObject();
            }
            await pctx.ExecuteQueryAsync();
            pctx.Load(lib.RootFolder, f => f.ServerRelativeUrl);
            await pctx.ExecuteQueryAsync();
            var targetPageUrl = $"{lib.RootFolder.ServerRelativeUrl}/{PageName}";
            await PageCopier.TryDeleteFileAsync(pctx, targetPageUrl);
            for (var v = 1; v <= 5; v++)
                await PageCopier.TryDeleteFileAsync(pctx, targetPageUrl.Replace(".aspx", $"({v}).aspx"));
        }

        // Decisive probe: enumerate source pages exactly like the copier does.
        using (var dbg = site.CreateContext())
        {
            var lib = dbg.Web.Lists.GetByTitle("Site Pages");
            var all = lib.GetItems(CamlQuery.CreateAllItemsQuery(100));
            dbg.Load(all);
            await dbg.ExecuteQueryAsync();
            Console.WriteLine("  source pages: " + string.Join(", ", all.AsEnumerable()
                .Where(i => i.FileSystemObjectType == FileSystemObjectType.File)
                .Select(i => $"{((string)i["FileRef"]).Split('/')[^1]}(v{i.FieldValues.GetValueOrDefault("_UIVersionString")})")));
        }

        // ---- Copy pages cross-site (subsite -> parent web) ----
        var copier = new PageCopier(site, Program.Source, CleverPoint.Migrator.Core.Model.ExistingItemMode.Skip);
        var result = await copier.CopyPagesAsync();
        Console.WriteLine($"  pages: {result.Summary()}");
        foreach (var r in result.Records.Where(r => r.Status == ItemCopyStatus.Copied).Take(3))
            Console.WriteLine($"    [Copied] {r.SourcePath.Split('/')[^1]}: {r.Message}");
        foreach (var r in result.Records.Where(r => r.Status == ItemCopyStatus.Failed).Take(5))
            Console.WriteLine($"    [Failed] {r.SourcePath}: {r.Message}");
        Program.Check("pages: no failures", result.Failed == 0, result.Summary());
        var copiedRecord = result.Records.FirstOrDefault(r =>
            r.ItemType == "Page" && r.SourcePath.EndsWith(ActualPageName) && r.Status == ItemCopyStatus.Copied);
        Program.Check("pages: test page copied", copiedRecord != null, ActualPageName);
        if (copiedRecord == null) return;

        // ---- Verify the ACTUAL copied target: canvas, URL rewrite, published ----
        using (var vctx = Program.Source.CreateContext())
        {
            vctx.Load(vctx.Web, w => w.Url);
            await vctx.ExecuteQueryAsync();
            var webUrl = vctx.Web.Url.TrimEnd('/');

            var copiedFile = vctx.Web.GetFileByServerRelativeUrl(copiedRecord.TargetPath);
            vctx.Load(copiedFile.ListItemAllFields, i => i.Id);
            vctx.Load(copiedFile, f => f.MinorVersion, f => f.MajorVersion);
            await vctx.ExecuteQueryAsync();
            using var doc = await Program.Source.Rest.GetJsonAsync(
                $"{webUrl}/_api/sitepages/pages({copiedFile.ListItemAllFields.Id})");
            var canvas = doc.RootElement.TryGetProperty("CanvasContent1", out var c) ? c.GetString() ?? "" : "";
            Program.Check("pages: canvas content arrived", canvas.Contains("Hello from the migrator test"),
                $"{canvas.Length} chars");
            // SharePoint normalizes hrefs to server-relative on save; accept either form.
            var targetServerRel = new Uri(webUrl).AbsolutePath;
            Program.Check("pages: link href survived + rewritten to target web",
                (canvas.Contains($"{webUrl}/Shared%20Documents") || canvas.Contains($"{targetServerRel}/Shared%20Documents"))
                    && !canvas.Contains("/migtest/Shared"),
                canvas.Length > 300 ? canvas[240..300] : canvas);
            Program.Check("pages: page is published", copiedFile.MinorVersion == 0,
                $"v{copiedFile.MajorVersion}.{copiedFile.MinorVersion}");
        }
    }
}

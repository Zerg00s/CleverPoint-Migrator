using CleverPoint.Migrator.Core.Http;

namespace CleverPoint.Migrator.Core.Reports.Definitions;

/// <summary>
/// Lists people keep in their OneDrive: what Microsoft Lists calls "My lists". A file migration of a
/// OneDrive does not carry them, so they have to be known about beforehand.
/// </summary>
public sealed class OneDriveListsReport : ReportDefinition
{
    public override string Id => "onedrive-lists";
    public override string Title => "Lists in OneDrives";
    public override string Group => "Discovery";
    public override string Summary => "Lists that people created in their OneDrive (\"My lists\" in Microsoft Lists). They are not files, so a OneDrive file migration leaves them behind.";
    public override string? DefaultSiteType => "onedrive";

    public override IReadOnlyList<ReportTable> Tables => new[]
    {
        Table("lists", "Lists",
            Text("listTitle"), Text("listType"), Number("itemCount"), Date("created"), Date("lastModified"), Text("description"), Text("listUrl"), Text("listId")),
    };

    /// <summary>Lists every OneDrive has that nobody made by hand.</summary>
    private static readonly HashSet<string> BuiltIn = new(StringComparer.OrdinalIgnoreCase)
    {
        "Social", "MicroFeed", "Sharing Links", "Access Requests", "TaxonomyHiddenList", "User Information List", "appdata", "appfiles",
        "Composed Looks", "Converted Forms", "Workflow Tasks", "Workflow History", "Content type publishing error log", "Maintenance Log Library",
    };

    public override async Task RunAsync(ReportContext context)
    {
        var site = context.Connection.SiteUrl;
        var authority = new Uri(site).GetLeftPart(UriPartial.Authority);
        // BaseType 0 is a list (as opposed to a library, a survey or a discussion board).
        var lists = await context.Connection.Rest.GetAllAsync(
            $"{site}/_api/web/lists?$select=Id,Title,Description,ItemCount,Hidden,Created,LastItemModifiedDate,BaseTemplate,BaseType,IsCatalog,IsApplicationList,IsPrivate," +
            "RootFolder/ServerRelativeUrl&$expand=RootFolder&$filter=BaseType eq 0 and Hidden eq false&$top=500", context.Cancel);
        var found = 0;
        foreach (var list in lists)
        {
            if (list.Bool("IsCatalog") || list.Bool("IsApplicationList") || BuiltIn.Contains(list.Str("Title"))) continue;
            context.Write("lists", new Dictionary<string, object?>
            {
                ["listTitle"] = list.Str("Title"),
                ["listType"] = ListTypes.Name(list.Num("BaseTemplate")),
                ["itemCount"] = list.Num("ItemCount"),
                ["created"] = list.When("Created"),
                ["lastModified"] = list.When("LastItemModifiedDate"),
                ["description"] = list.Str("Description"),
                ["listUrl"] = ListTypes.Url(site, list.Child("RootFolder").Str("ServerRelativeUrl")),
                ["listId"] = list.Str("Id"),
            });
            found++;
            context.Count("items", list.Num("ItemCount"));
        }
        context.Count("lists", found);
        if (found > 0) context.Count("sitesWithLists");
    }
}

/// <summary>Pages people made in their OneDrive.</summary>
public sealed class OneDrivePagesReport : ReportDefinition
{
    public override string Id => "onedrive-pages";
    public override string Title => "Pages in OneDrives";
    public override string Group => "Discovery";
    public override string Summary => "Site pages that exist in OneDrives. A OneDrive has no pages library until someone creates a page there, and a file migration does not move them.";
    public override string? DefaultSiteType => "onedrive";

    public override IReadOnlyList<ReportOption> Options => new[]
    {
        new ReportOption("includeTemplates", "bool", false, "Also list page templates and the pages SharePoint creates by itself."),
    };

    public override IReadOnlyList<ReportTable> Tables => new[]
    {
        Table("pages", "Pages",
            Text("library"), Text("folder"), Text("pageName"), Number("sizeBytes"), Date("created"), Text("createdBy"), Date("modified"), Text("modifiedBy"),
            Text("version"), Text("pageUrl")),
    };

    public override async Task RunAsync(ReportContext context)
    {
        var site = context.Connection.SiteUrl;
        var authority = new Uri(site).GetLeftPart(UriPartial.Authority);
        var includeTemplates = context.Options.Bool("includeTemplates");
        // 119 is a pages library ("Site Pages"); 850 is the classic publishing "Pages" library.
        var libraries = await context.Connection.Rest.GetAllAsync(
            $"{site}/_api/web/lists?$select=Id,Title,ItemCount,BaseTemplate,RootFolder/ServerRelativeUrl&$expand=RootFolder" +
            "&$filter=BaseTemplate eq 119 or BaseTemplate eq 850&$top=100", context.Cancel);
        var pages = 0;
        foreach (var library in libraries)
        {
            if (library.Num("ItemCount") == 0) continue;
            var root = library.Child("RootFolder").Str("ServerRelativeUrl");
            try
            {
                await FileWalker.ForEachAsync(context.Connection, site, library.Str("Id"), entry =>
                {
                    if (entry.IsFolder || !entry.Name.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase)) return;
                    var folder = DocumentsReport.Relative(entry.Folder, root);
                    if (!includeTemplates && (folder.StartsWith("Templates", StringComparison.OrdinalIgnoreCase) || folder.StartsWith("Forms", StringComparison.OrdinalIgnoreCase))) return;
                    context.Write("pages", new Dictionary<string, object?>
                    {
                        ["library"] = library.Str("Title"),
                        ["folder"] = folder,
                        ["pageName"] = entry.Name,
                        ["sizeBytes"] = entry.Size,
                        ["created"] = entry.Created,
                        ["createdBy"] = entry.CreatedBy,
                        ["modified"] = entry.Modified,
                        ["modifiedBy"] = entry.ModifiedBy,
                        ["version"] = entry.Version,
                        ["pageUrl"] = authority + Links.Escape(entry.Path),
                    });
                    pages++;
                }, context.Cancel);
            }
            catch (Exception ex) when (Gone.Is(ex)) { context.Count(Gone.Counter); }
        }
        context.Count("pages", pages);
        if (pages > 0) context.Count("sitesWithPages");
    }
}

/// <summary>Site collection app catalogs and the apps in them.</summary>
public sealed class SiteAppCatalogsReport : ReportDefinition
{
    public override string Id => "site-app-catalogs";
    public override string Title => "Site app catalogs";
    public override string Group => "Discovery";
    public override string Summary => "Sites that have their own app catalog, and the apps in it: version, whether it is deployed and installed. These apps exist only in that site and are easy to miss in a migration. Given the tenant's app catalog site, it lists the tenant's apps.";

    public override IReadOnlyList<ReportOption> Options => new[]
    {
        new ReportOption("includeSitesWithout", "bool", false, "Also write a row for sites that have no app catalog."),
    };

    public override IReadOnlyList<ReportTable> Tables => new[]
    {
        Table("apps", "Apps",
            Text("catalog"), Text("appTitle"), Text("catalogVersion"), Text("installedVersion"), Flag("deployed"), Flag("installed"), Flag("enabled"), Flag("canUpgrade"),
            Flag("clientSideSolution"), Flag("containsTenantWideExtension"), Text("productId"), Text("appId")),
        Table("catalogs", "Catalogs", Flag("hasAppCatalog"), Text("catalog"), Number("apps"), Text("catalogUrl")),
    };

    public override async Task RunAsync(ReportContext context)
    {
        var site = context.Connection.SiteUrl;
        var authority = new Uri(site).GetLeftPart(UriPartial.Authority);
        // An app catalog is a library of its own kind in the site: template 336 in a site collection,
        // 330 in the tenant's catalog site. Its presence is the dependable sign; asking for the apps
        // of a site that has no catalog is an error, not an empty answer.
        var catalogs = await context.Connection.Rest.GetAllAsync(
            $"{site}/_api/web/lists?$select=Title,BaseTemplate,RootFolder/ServerRelativeUrl&$expand=RootFolder&$filter=BaseTemplate eq 336 or BaseTemplate eq 330&$top=5",
            context.Cancel);
        if (catalogs.Count == 0)
        {
            context.Count("sitesWithoutCatalog");
            if (context.Options.Bool("includeSitesWithout"))
                context.Write("catalogs", new Dictionary<string, object?> { ["hasAppCatalog"] = false, ["apps"] = 0 });
            return;
        }

        // The tenant's own app catalog is a site of a kind of its own and answers on a different address.
        using var web = await context.Connection.Rest.GetJsonAsync($"{site}/_api/web?$select=WebTemplate");
        var tenantCatalog = web.RootElement.Str("WebTemplate").Equals("APPCATALOG", StringComparison.OrdinalIgnoreCase);
        var kind = tenantCatalog ? "Tenant" : "Site collection";
        var apps = new List<System.Text.Json.JsonElement>();
        try
        {
            apps = await context.Connection.Rest.GetAllAsync(
                $"{site}/_api/web/{(tenantCatalog ? "tenantappcatalog" : "sitecollectionappcatalog")}/AvailableApps", context.Cancel);
        }
        catch (SpRestException ex) when (ex.StatusCode is 400 or 404)
        {
            // The library is there but the catalog is not active for this site any more.
            context.Count("catalogsNotActive");
        }
        foreach (var app in apps)
        {
            var installed = app.Str("InstalledVersion");
            context.Write("apps", new Dictionary<string, object?>
            {
                ["catalog"] = kind,
                ["appTitle"] = app.Str("Title"),
                ["catalogVersion"] = app.Str("AppCatalogVersion"),
                ["installedVersion"] = installed,
                ["deployed"] = app.Bool("Deployed") ? true : null,
                ["installed"] = installed.Length > 0 ? true : null,
                ["enabled"] = app.Bool("IsEnabled") ? true : null,
                ["canUpgrade"] = app.Bool("CanUpgrade") ? true : null,
                ["clientSideSolution"] = app.Bool("IsClientSideSolution") ? true : null,
                ["containsTenantWideExtension"] = app.Bool("ContainsTenantWideExtension") ? true : null,
                ["productId"] = app.Str("ProductId"),
                ["appId"] = app.Str("ID"),
            });
            context.Count("apps");
            if (installed.Length > 0) context.Count("installedApps");
        }
        context.Write("catalogs", new Dictionary<string, object?>
        {
            ["hasAppCatalog"] = true,
            ["catalog"] = kind,
            ["apps"] = apps.Count,
            ["catalogUrl"] = authority + Links.Escape(catalogs[0].Child("RootFolder").Str("ServerRelativeUrl")),
        });
        context.Count("sitesWithCatalog");
    }
}

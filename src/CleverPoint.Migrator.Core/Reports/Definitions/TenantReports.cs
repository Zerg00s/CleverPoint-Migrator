namespace CleverPoint.Migrator.Core.Reports.Definitions;

/// <summary>Every site collection of a tenant, with what the admin center shows about each.</summary>
public sealed class SitesReport : ReportDefinition
{
    public override string Id => "sites";
    public override string Title => "Sites";
    public override string Group => "Inventory";
    public override string Summary => "Every site collection of the tenant: owner, template, storage, sharing, hub, group or channel, lock state and last activity.";
    public override ReportScope Scope => ReportScope.Tenant;
    public override bool NeedsTenantAdmin => true;

    public override IReadOnlyList<ReportOption> Options => new[]
    {
        new ReportOption("includeDeleted", "bool", false, "Also list sites that are in the recycle bin."),
        new ReportOption("includeSystem", "bool", false, "Also list SharePoint's own sites: search center, app catalog, OneDrive host, redirect sites."),
    };

    public override IReadOnlyList<ReportTable> Tables => new[]
    {
        Table("sites", "Sites",
            Text("url"), Text("title"), Text("siteType"), Text("template"), Text("templateName"), Text("owner"),
            Text("connection"), Text("hub"), Number("storageUsedBytes"), Number("storageUsedGB"), Number("archivedBytes"), Text("archiveStatus"),
            Number("fileCount"), Number("pageViews"), Date("lastActivity"), Text("externalSharing"), Text("lockState"), Text("language"),
            Flag("deleted"), Date("timeDeleted"), Flag("systemSite"), Text("siteId"), Text("groupId"), Text("hubSiteId")),
    };

    public override async Task RunAsync(ReportContext context)
    {
        var includeDeleted = context.Options.Bool("includeDeleted");
        var includeSystem = context.Options.Bool("includeSystem");
        var locks = await TenantSites.LockStatesAsync(context.Connection, context.Phase, context.Cancel);
        await TenantSites.ForEachSiteAsync(context.Connection, item =>
        {
            var url = item.Str("SiteUrl");
            var template = item.Str("TemplateName");
            // OneDrives have their own report; the few that show up in this list are left to it.
            if (TenantSites.IsOneDrive(url, template)) return;
            var system = TenantSites.IsSystemSite(url, template);
            var deletedOn = item.When("TimeDeleted");
            if (system && !includeSystem) return;
            if (deletedOn is not null && !includeDeleted) return;

            var siteId = item.Id("SiteId");
            var hubId = item.Id("HubSiteId");
            var groupId = item.Id("GroupId");
            var channel = item.Num("ChannelType");
            var storage = item.Num("StorageUsed");
            context.Write("sites", new Dictionary<string, object?>
            {
                ["url"] = url,
                ["title"] = item.Str("Title"),
                ["siteType"] = TenantSites.SiteType(url, template),
                ["template"] = template,
                ["templateName"] = TenantSites.TemplateName(template),
                ["owner"] = item.Str("SiteOwnerEmail"),
                ["connection"] = channel == 1 ? "Private channel" : channel == 2 ? "Shared channel" : groupId != Guid.Empty ? "Group" : "Standalone",
                ["hub"] = hubId == Guid.Empty ? "" : hubId == siteId ? "Hub" : "Associated",
                ["storageUsedBytes"] = storage,
                ["storageUsedGB"] = Math.Round(storage / 1073741824.0, 2),
                ["archivedBytes"] = item.Num("ArchivedFileDiskUsed") is var archived and > 0 ? archived : null,
                ["archiveStatus"] = item.Str("ArchiveStatus") is { Length: > 0 } status && status != "None" ? status : null,
                ["fileCount"] = item.Num("NumOfFiles"),
                ["pageViews"] = item.Num("PageViews"),
                ["lastActivity"] = item.When("LastActivityOn"),
                ["externalSharing"] = item.Str("ExternalSharing"),
                ["lockState"] = TenantSites.LockStateName(locks.GetValueOrDefault(url.TrimEnd('/')) ?? ""),
                ["language"] = TenantSites.Language(item.Num("LCID")),
                ["deleted"] = deletedOn is not null ? true : null,
                ["timeDeleted"] = deletedOn,
                ["systemSite"] = system ? true : null,
                ["siteId"] = siteId == Guid.Empty ? null : siteId.ToString(),
                ["groupId"] = groupId == Guid.Empty ? null : groupId.ToString(),
                ["hubSiteId"] = hubId == Guid.Empty ? null : hubId.ToString(),
            });
            context.Count("sites");
            context.Count("storageBytes", storage);
            context.Count("files", item.Num("NumOfFiles"));
            if (groupId != Guid.Empty) context.Count("groupConnected");
            if (locks.GetValueOrDefault(url.TrimEnd('/')) is { Length: > 0 } lockState && !lockState.Equals("Unlock", StringComparison.OrdinalIgnoreCase)) context.Count("locked");
            if (hubId != Guid.Empty && hubId == siteId) context.Count("hubs");
        }, context.Phase, context.Cancel);
    }
}

/// <summary>Every OneDrive of a tenant.</summary>
public sealed class OneDrivesReport : ReportDefinition
{
    public override string Id => "onedrives";
    public override string Title => "OneDrives";
    public override string Group => "Inventory";
    public override string Summary => "Every OneDrive of the tenant: owner, storage used and quota, lock state, archive state and last change.";
    public override ReportScope Scope => ReportScope.Tenant;
    public override bool NeedsTenantAdmin => true;

    public override IReadOnlyList<ReportTable> Tables => new[]
    {
        Table("onedrives", "OneDrives",
            Text("url"), Text("ownerEmail"), Text("ownerName"), Text("owner"), Text("title"), Text("template"), Text("status"), Text("lockState"),
            Date("lastContentModified"), Number("storageUsedMB"), Number("storageUsedBytes"), Number("storageQuotaMB"), Number("storageUsedPercent"),
            Text("archiveStatus")),
    };

    public override async Task RunAsync(ReportContext context)
    {
        await TenantSites.ForEachOneDriveAsync(context.Connection, site =>
        {
            var usedMb = site.StorageUsage;
            var quotaMb = site.StorageMaximumLevel;
            context.Write("onedrives", new Dictionary<string, object?>
            {
                ["url"] = site.Url,
                ["ownerEmail"] = Optional(() => site.OwnerEmail),
                ["ownerName"] = Optional(() => site.OwnerName),
                ["owner"] = site.Owner,
                ["title"] = site.Title,
                ["template"] = site.Template,
                ["status"] = site.Status,
                ["lockState"] = site.LockState,
                ["lastContentModified"] = site.LastContentModifiedDate.Year > 1900 ? site.LastContentModifiedDate : null,
                ["storageUsedMB"] = usedMb,
                ["storageUsedBytes"] = usedMb * 1048576L,
                ["storageQuotaMB"] = quotaMb,
                ["storageUsedPercent"] = quotaMb > 0 ? Math.Round(usedMb * 100.0 / quotaMb, 2) : null,
                ["archiveStatus"] = Optional(() => site.ArchiveStatus) is { Length: > 0 } status && status != "None" ? status : null,
            });
            context.Count("onedrives");
            context.Count("storageBytes", usedMb * 1048576L);
            if (!string.Equals(site.LockState, "Unlock", StringComparison.OrdinalIgnoreCase)) context.Count("locked");
        }, context.Phase, context.Cancel);
    }

    /// <summary>A property an older tenant may not return at all; reading one that was not sent throws.</summary>
    private static T? Optional<T>(Func<T> read)
    {
        try { return read(); }
        catch (Microsoft.SharePoint.Client.PropertyOrFieldNotInitializedException) { return default; }
    }
}

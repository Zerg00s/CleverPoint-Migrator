using System.Globalization;
using System.Text.Json;
using CleverPoint.Migrator.Core.Csom;
using Microsoft.Online.SharePoint.TenantAdministration;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.Core.Reports.Definitions;

/// <summary>
/// What limits every library works by, next to what the site and the tenant say new libraries get,
/// and how far along SharePoint's own background jobs are. Nothing is scanned file by file, so this
/// is quick over hundreds of sites.
/// </summary>
public sealed class VersionLimitsReport : ReportDefinition
{
    public override string Id => "version-limits";
    public override string Title => "Version history limits";
    public override string Group => "Inventory";
    public override string Summary => "How many versions each library keeps and for how long, next to the site's and the tenant's defaults. Finds libraries that keep versions forever, and follows SharePoint's limit and trim jobs.";

    public override IReadOnlyList<ReportOption> Options => new[]
    {
        new ReportOption("includeSubsites", "bool", true, "Also look at the libraries of every subsite."),
        new ReportOption("includeHidden", "bool", false, "Also list hidden libraries."),
    };

    public override IReadOnlyList<ReportTable> Tables => new[]
    {
        // What a site is set to, then what its libraries do in fact, the ones that keep versions forever first:
        // that is the number the report is run for, and the sites with the most of them come first.
        Table("limits", "Limits by site",
            Text("siteSetting"), Number("versionedLibraries"), Number("keptForever"), Number("automatic"), Number("deletedAfterDays"), IfAny(Text("tenantDefault")),
            IfAny(Text("limitsJob")), IfAny(Number("limitsJobLibraries")), IfAny(Text("trimJob")), IfAny(Text("trimJobKind")), IfAny(Date("trimJobRequested")),
            IfAny(Number("versionsDeleted")), IfAny(Number("storageReleasedBytes")), IfAny(Text("note"))) with { SortBy = "keptForever" },
        Table("libraries", "Libraries",
            Text("webUrl"), Text("library"), Text("versionLimits"), Number("majorVersionLimit"), Number("expireAfterDays"), Number("items"), Flag("hidden"), Text("libraryUrl")),
    };

    public override async Task RunAsync(ReportContext context)
    {
        var site = context.Connection.SiteUrl;
        var includeHidden = context.Options.Bool("includeHidden");
        int versioned = 0, automatic = 0, expiring = 0, forever = 0;
        foreach (var web in await WebWalker.WebsAsync(context, context.Options.Bool("includeSubsites")))
        {
            foreach (var library in await VersionLimits.LibrariesAsync(context.Connection, web, context.Cancel))
            {
                if (library.Hidden && !includeHidden) continue;
                context.Write("libraries", new Dictionary<string, object?>
                {
                    ["webUrl"] = web,
                    ["library"] = library.Title,
                    ["versionLimits"] = library.Limits,
                    ["majorVersionLimit"] = library.Versioning ? library.MajorVersions : null,
                    ["expireAfterDays"] = library.Versioning && library.TrimMode == 1 ? library.ExpireAfterDays : null,
                    ["items"] = library.Items,
                    ["hidden"] = library.Hidden ? true : null,
                    ["libraryUrl"] = library.Url,
                });
                context.Count("libraries");
                if (!library.Versioning) continue;
                versioned++;
                if (library.TrimMode == 2) automatic++;
                else if (library.TrimMode == 1) expiring++;
                else forever++;
            }
        }

        var row = new Dictionary<string, object?>
        {
            ["tenantDefault"] = await VersionLimits.TenantAsync(context.Connect, site),
            ["siteSetting"] = await VersionLimits.SiteAsync(context.Connection, site),
            ["versionedLibraries"] = versioned,
            ["automatic"] = automatic,
            ["deletedAfterDays"] = expiring,
            ["keptForever"] = forever,
        };
        await VersionJobs.ReadAsync(context, site, row);
        context.Write("limits", row);
        context.Count("librariesKeepingVersionsForever", forever);
        if (forever > 0) context.Count("sitesWithSuchLibraries");
    }
}

/// <summary>The background jobs SharePoint runs for a site: changing the limits of its libraries, and deleting old versions.</summary>
internal static class VersionJobs
{
    /// <summary>Adds what is known about a site's jobs to a row. Only a connection to the admin site can ask.</summary>
    public static async Task ReadAsync(ReportContext context, string site, Dictionary<string, object?> row)
    {
        try
        {
            var admin = await context.Connect(ReportSpecs.AdminUrl(site));
            using var csom = admin.CreateContext();
            var tenant = new Tenant(csom);
            var trim = tenant.GetFileVersionBatchDeleteJobProgress(site);
            var limits = tenant.GetVersionPolicyForDocLibsJobProgress(site);
            await csom.ExecuteWithRetryAsync();
            if (VersionLimits.Job(limits.Value) is { } limitsJob)
            {
                row["limitsJob"] = VersionLimits.JobStatus(limitsJob);
                row["limitsJobLibraries"] = Number(limitsJob, "ListsProcessedInTotal");
            }
            if (VersionLimits.Job(trim.Value) is { } trimJob)
            {
                row["trimJob"] = VersionLimits.JobStatus(trimJob);
                row["trimJobKind"] = TrimKind(trimJob);
                row["trimJobRequested"] = When(trimJob, "RequestTimeInUTC");
                row["versionsDeleted"] = Number(trimJob, "VersionsDeleted");
                row["storageReleasedBytes"] = Number(trimJob, "StorageReleasedInBytes") ?? Number(trimJob, "StorageReleased");
                context.Count("sitesWithATrimJob");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            row["note"] = "Jobs not read: that takes a connection to the tenant's admin site.";
        }
    }

    /// <summary>Whether a job is still waiting or working.</summary>
    public static bool Running(JsonElement job) => job.Str("Status") is var status
        && (status.Equals("New", StringComparison.OrdinalIgnoreCase) || status.Equals("InProgress", StringComparison.OrdinalIgnoreCase));

    private static string TrimKind(JsonElement job)
    {
        var mode = job.TryGetProperty("BatchDeleteMode", out var value) ? (value.ValueKind == JsonValueKind.Number ? value.GetInt32().ToString(CultureInfo.InvariantCulture) : value.GetString() ?? "") : "";
        return mode switch
        {
            "2" or "AutomaticTrim" => "Automatic",
            "1" or "CountLimits" => $"Keep the newest {Number(job, "MajorVersionLimit"):N0}",
            "0" or "DeleteOlderThanDays" => "Older than a number of days",
            _ => mode,
        };
    }

    // SharePoint sends these numbers and dates sometimes as text.
    private static long? Number(JsonElement job, string name)
    {
        if (!job.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static DateTime? When(JsonElement job, string name) =>
        job.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && DateTime.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when) && when.Year > 2000
            ? when
            : null;
}

/// <summary>Changes a site's version history limits, and can queue the job that deletes what the new limits no longer keep.</summary>
public sealed class SetVersionLimitsAction : ReportDefinition
{
    public override string Id => "set-version-limits";
    public override string Title => "Set version history limits";
    public override string Group => "Actions";
    public override string Summary => "Switch sites to automatic version history limits, or limits you choose, for existing and new libraries. Can also queue SharePoint's job that deletes versions the limits no longer keep.";
    public override bool NeedsTenantAdmin => true;
    public override bool ChangesData => true;
    public override string? ApplyWarning => "Changing limits deletes nothing by itself. A trim job does: the versions it deletes do not go to the recycle bin and cannot be brought back.";

    public override IReadOnlyList<ReportOption> Options => new[]
    {
        new ReportOption("limits", "text", "",
            "Required. Automatic = SharePoint decides which versions are still worth keeping: more of the recent ones, fewer of the old. Manual = the numbers in majorVersions and expireAfterDays. TenantDefault = the site gives up its own setting; that only affects libraries made later.",
            new[] { "Automatic", "Manual", "TenantDefault" }),
        new ReportOption("majorVersions", "int", 500, "With Manual: how many versions of a file to keep, up to 50000.", When: "limits=Manual"),
        new ReportOption("expireAfterDays", "int", 0, "With Manual: delete versions older than this many days, 30 or more. 0 keeps versions until there are too many.", When: "limits=Manual"),
        new ReportOption("existingLibraries", "bool", true, "Also change the libraries the site already has. SharePoint does that as a background job that can take a day. Without it only libraries made from now on get the limits."),
        new ReportOption("trim", "text", "None",
            "Queue a job that deletes existing versions, permanently. None = delete nothing. Automatic = the versions automatic limits would not have kept. OlderThanDays = versions older than trimDays. KeepNewest = all but the newest trimKeep versions of each file.",
            new[] { "None", "Automatic", "OlderThanDays", "KeepNewest" }),
        new ReportOption("trimDays", "int", 180, "With trim OlderThanDays: the age in days, 30 or more.", When: "trim=OlderThanDays"),
        new ReportOption("trimKeep", "int", 100, "With trim KeepNewest: how many versions of each file stay.", When: "trim=KeepNewest"),
    };

    public override IReadOnlyList<ReportTable> Tables => new[]
    {
        Table("changes", "Changes",
            Text("limitsBefore"), Text("limitsAfter"), Text("action"), IfAny(Number("librariesToChange")), IfAny(Text("existingLibraries")), IfAny(Text("trim")), IfAny(Text("note"))),
        // Every library whose limits change, so that the preview names what is touched.
        Table("libraries", "Libraries", Text("webUrl"), Text("library"), Text("limitsNow"), Text("limitsAfter"), Text("libraryUrl")),
    };

    private static string? Mode(ReportOptions options) => options.Text("limits").Trim().ToLowerInvariant() switch
    {
        "automatic" or "auto" => "Automatic",
        "manual" => "Manual",
        "tenantdefault" or "tenant-default" or "inherit" => "TenantDefault",
        _ => null,
    };

    private static string? Trim(ReportOptions options) => options.Text("trim").Trim().ToLowerInvariant() switch
    {
        "" or "none" => "None",
        "automatic" or "auto" => "Automatic",
        "olderthandays" or "older-than-days" => "OlderThanDays",
        "keepnewest" or "keep-newest" => "KeepNewest",
        _ => null,
    };

    public override IEnumerable<string> CheckOptions(ReportOptions options)
    {
        var mode = Mode(options);
        if (mode is null)
        {
            var given = options.Text("limits").Trim();
            yield return given.Length == 0
                ? "Say which limits to set: the setting limits takes Automatic, Manual or TenantDefault."
                : $"'{given}' is not a kind of limit. Use Automatic, Manual or TenantDefault.";
        }
        if (mode == "Manual")
        {
            if (options.Int("majorVersions") is < 1 or > 50000) yield return "majorVersions is a number from 1 to 50000.";
            if (options.Int("expireAfterDays") is var days && days != 0 && days is < 30 or > 36500) yield return "expireAfterDays is 0, or a number of days from 30 to 36500.";
        }
        var trim = Trim(options);
        if (trim is null) yield return $"'{options.Text("trim").Trim()}' is not a kind of trim. Use None, Automatic, OlderThanDays or KeepNewest.";
        if (trim == "OlderThanDays" && options.Int("trimDays") < 30) yield return "trimDays is 30 or more: SharePoint does not delete versions younger than that by age.";
        if (trim == "KeepNewest" && options.Int("trimKeep") is < 1 or > 50000) yield return "trimKeep is a number from 1 to 50000.";
    }

    public override async Task RunAsync(ReportContext context)
    {
        if (CheckOptions(context.Options).FirstOrDefault() is { } problem) throw new InvalidOperationException(problem);
        var mode = Mode(context.Options)!;
        var trim = Trim(context.Options)!;
        var major = context.Options.Int("majorVersions");
        var days = context.Options.Int("expireAfterDays");
        var url = context.Site.Url;

        var admin = await context.Connect(ReportSpecs.AdminUrl(url));
        using var csom = admin.CreateContext();
        var tenant = new Tenant(csom);
        var site = tenant.GetSitePropertiesByUrl(url, includeDetail: true);
        csom.Load(site, s => s.InheritVersionPolicyFromTenant, s => s.EnableAutoExpirationVersionTrim, s => s.MajorVersionLimit, s => s.ExpireVersionsAfterDays);
        var trimProgress = tenant.GetFileVersionBatchDeleteJobProgress(url);
        await csom.ExecuteWithRetryAsync();

        var before = site.InheritVersionPolicyFromTenant
            ? VersionLimits.TenantDefault
            : VersionLimits.Describe(site.EnableAutoExpirationVersionTrim, site.MajorVersionLimit, site.ExpireVersionsAfterDays);
        var after = mode switch
        {
            "Automatic" => VersionLimits.Automatic,
            "Manual" => VersionLimits.Describe(false, major, days),
            _ => VersionLimits.TenantDefault,
        };

        // The libraries that work by other limits than the ones asked for. Giving up the site's own
        // setting says nothing about what its libraries should have, so then there is nothing to compare.
        var differing = 0;
        if (mode != "TenantDefault" && context.Options.Bool("existingLibraries"))
            foreach (var web in await WebWalker.WebsAsync(context, includeSubsites: true))
                foreach (var library in await VersionLimits.LibrariesAsync(context.Connection, web, context.Cancel))
                {
                    if (library.Hidden || !library.Versioning || library.Limits == after) continue;
                    differing++;
                    context.Write("libraries", new Dictionary<string, object?>
                    {
                        ["webUrl"] = web, ["library"] = library.Title, ["limitsNow"] = library.Limits, ["limitsAfter"] = after, ["libraryUrl"] = library.Url,
                    });
                }

        var changeSetting = before != after;
        var changeLibraries = differing > 0;
        var row = new Dictionary<string, object?> { ["limitsBefore"] = before, ["limitsAfter"] = after, ["librariesToChange"] = changeLibraries ? differing : null };
        if (trim != "None" && VersionLimits.Job(trimProgress.Value) is { } running && VersionJobs.Running(running))
            row["note"] = "A job that deletes versions is already waiting or running for this site. SharePoint may refuse a second one.";

        if (!changeSetting && !changeLibraries && trim == "None")
        {
            row["action"] = "Already " + after;
            context.Write("changes", row);
            context.Count("unchanged");
            return;
        }

        var trimText = trim switch
        {
            "Automatic" => "delete the versions automatic limits would not have kept",
            "OlderThanDays" => $"delete versions older than {context.Options.Int("trimDays"):N0} days",
            "KeepNewest" => $"delete all but the newest {context.Options.Int("trimKeep"):N0} versions of each file",
            _ => "",
        };

        if (!context.Apply)
        {
            row["action"] = changeSetting ? "Would set to " + after : "The site's setting is already " + after;
            if (changeLibraries) row["existingLibraries"] = $"Would start a job that changes {differing:N0} libraries";
            if (trim != "None") row["trim"] = $"Would queue a job to {trimText}. Deleted versions cannot be brought back.";
            context.Write("changes", row);
            context.Count("wouldChange");
            return;
        }

        if (changeSetting || changeLibraries)
        {
            // Every one of these is sent with the update, so every one is given a value: SharePoint refuses
            // a number left at its default beside a setting it does not go with. -1 is how a number that
            // does not apply is left out, which is what SharePoint's own tools send.
            if (mode == "TenantDefault")
            {
                site.InheritVersionPolicyFromTenant = true;
                site.EnableAutoExpirationVersionTrim = false;
                site.ApplyToNewDocumentLibraries = false;
                site.ApplyToExistingDocumentLibraries = false;
                site.MajorVersionLimit = -1;
                site.MajorWithMinorVersionsLimit = -1;
                site.ExpireVersionsAfterDays = -1;
            }
            else
            {
                var auto = mode == "Automatic";
                site.InheritVersionPolicyFromTenant = false;
                site.EnableAutoExpirationVersionTrim = auto;
                site.ApplyToNewDocumentLibraries = true;
                site.ApplyToExistingDocumentLibraries = changeLibraries;
                site.MajorVersionLimit = auto ? -1 : major;
                site.ExpireVersionsAfterDays = auto ? -1 : days;
                // For libraries that keep drafts: 0 leaves the number of versions whose drafts are kept unlimited,
                // as it is in a library nobody has set it for. It is only asked for when existing libraries change.
                site.MajorWithMinorVersionsLimit = !auto && changeLibraries ? 0 : -1;
            }
            var operation = site.Update();
            csom.Load(operation, o => o.IsComplete);
            await csom.ExecuteWithRetryAsync();
            // The change is queued; wait for it so the row says what is true, within reason.
            for (var attempt = 0; attempt < 30 && !operation.IsComplete; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), context.Cancel);
                operation.RefreshLoad();
                await csom.ExecuteWithRetryAsync();
            }
            row["action"] = !changeSetting ? "The site's setting was already " + after
                : operation.IsComplete ? "Set to " + after : "Requested; SharePoint has not confirmed it yet";
            if (changeLibraries) row["existingLibraries"] = $"Job started for {differing:N0} libraries. It can take a day.";
        }
        else
        {
            row["action"] = "The site's setting was already " + after;
        }

        if (trim != "None")
        {
            var parameters = new FileVersionBatchDeleteParameters
            {
                BatchDeleteMode = trim switch
                {
                    "Automatic" => FileVersionBatchDeleteMode.AutomaticTrim,
                    "OlderThanDays" => FileVersionBatchDeleteMode.DeleteOlderThanDays,
                    _ => FileVersionBatchDeleteMode.CountLimits,
                },
                DeleteOlderThanDays = trim == "OlderThanDays" ? context.Options.Int("trimDays") : -1,
                MajorVersionLimit = trim == "KeepNewest" ? context.Options.Int("trimKeep") : -1,
                MajorWithMinorVersionsLimit = trim == "KeepNewest" ? 0 : -1,
            };
            tenant.NewFileVersionBatchDeleteJob(url, parameters);
            await csom.ExecuteWithRetryAsync();
            row["trim"] = $"Job queued to {trimText}. Follow it with the Version history limits report.";
            context.Count("trimJobsQueued");
        }

        context.Write("changes", row);
        context.Count("changed");
    }
}

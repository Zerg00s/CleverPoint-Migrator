using System.Text.Json;
using CleverPoint.Migrator.Core.Http;

namespace CleverPoint.Migrator.Core.Reports.Definitions;

/// <summary>Can the connection open each site. Never fails a site: the answer is the row.</summary>
public sealed class SiteAccessReport : ReportDefinition
{
    public override string Id => "site-access";
    public override string Title => "Site access check";
    public override string Group => "Discovery";
    public override string Summary => "Whether the connection can open each site: accessible, denied or not found. Worth running before a large report or migration.";

    public override IReadOnlyList<ReportTable> Tables => new[]
    {
        Table("access", "Access", Text("status"), Text("title"), Text("template"), Text("account"), Flag("siteAdmin"), Date("lastModified"), Text("error")),
    };

    public override async Task RunAsync(ReportContext context)
    {
        var row = new Dictionary<string, object?>();
        try
        {
            using var web = await context.Connection.Rest.GetJsonAsync(
                $"{context.Connection.SiteUrl}/_api/web?$select=Title,WebTemplate,Configuration,LastItemModifiedDate");
            row["status"] = "accessible";
            row["title"] = web.RootElement.Str("Title");
            row["template"] = web.RootElement.Str("WebTemplate") + "#" + web.RootElement.Str("Configuration");
            row["lastModified"] = web.RootElement.When("LastItemModifiedDate");
            try
            {
                using var user = await context.Connection.Rest.GetJsonAsync(
                    $"{context.Connection.SiteUrl}/_api/web/currentuser?$select=Title,LoginName,IsSiteAdmin");
                row["account"] = user.RootElement.Str("Title") is { Length: > 0 } title ? title : user.RootElement.Str("LoginName");
                row["siteAdmin"] = user.RootElement.Bool("IsSiteAdmin") ? true : null;
            }
            catch (SpRestException) { /* an app without a user identity on the site: access is what was asked */ }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var (status, error) = ReportRunner.Classify(ex);
            // A missing sign-in is the run's problem, not a property of this site.
            if (status is "signIn" or "noConnection") throw;
            row["status"] = status;
            row["error"] = error;
        }
        context.Write("access", row);
        context.Count((string)row["status"]!);
    }
}

/// <summary>Lists and libraries with their size and versioning settings.</summary>
public sealed class ListsReport : ReportDefinition
{
    public override string Id => "lists";
    public override string Title => "Lists and libraries";
    public override string Group => "Inventory";
    public override string Summary => "Every list and library: type, item count, dates, versioning limits, automatic version trimming, approval and check-out settings.";

    public override IReadOnlyList<ReportOption> Options => new[]
    {
        new ReportOption("includeHidden", "bool", false, "Also list hidden lists (system lists most people never see)."),
        new ReportOption("includeSubsites", "bool", false, "Also scan every subsite of each site."),
    };

    public override IReadOnlyList<ReportTable> Tables => new[]
    {
        Table("lists", "Lists",
            Text("webUrl"), Text("listTitle"), Text("listUrl"), Text("listType"), Number("baseTemplate"), Number("itemCount"), Flag("hidden"),
            Date("created"), Date("lastModified"), Text("versioning"), Number("majorVersionLimit"), Number("minorVersionLimit"),
            Text("versionTrimming"), Number("expireAfterDays"), Flag("contentApproval"), Flag("requireCheckout"), Text("draftVisibility"), Text("listId")),
    };

    public override async Task RunAsync(ReportContext context)
    {
        var includeHidden = context.Options.Bool("includeHidden");
        foreach (var web in await WebWalker.WebsAsync(context, context.Options.Bool("includeSubsites")))
        {
            context.Phase(web);
            var lists = await context.Connection.Rest.GetAllAsync(
                $"{web}/_api/web/lists?$select=Id,Title,ItemCount,Hidden,Created,LastItemModifiedDate,BaseTemplate,EnableVersioning,EnableMinorVersions," +
                "MajorVersionLimit,MajorWithMinorVersionsLimit,EnableModeration,ForceCheckout,DraftVersionVisibility,RootFolder/ServerRelativeUrl" +
                "&$expand=RootFolder&$top=500", context.Cancel);

            // Automatic version trimming lives in a separate resource and only libraries have it. One
            // call gets it for all of them; a tenant or account that cannot read it just leaves it blank.
            var policies = new Dictionary<Guid, JsonElement>();
            try
            {
                foreach (var library in await context.Connection.Rest.GetAllAsync(
                             $"{web}/_api/web/lists?$filter=BaseTemplate eq 101&$select=Id,VersionPolicies/DefaultExpireAfterDays,VersionPolicies/DefaultTrimMode" +
                             "&$expand=VersionPolicies&$top=500", context.Cancel))
                    policies[library.Id("Id")] = library.Child("VersionPolicies");
            }
            catch (SpRestException) { /* not available here */ }

            foreach (var list in lists)
            {
                var hidden = list.Bool("Hidden");
                if (hidden && !includeHidden) continue;
                var versioning = list.Bool("EnableVersioning");
                var minor = list.Bool("EnableMinorVersions");
                policies.TryGetValue(list.Id("Id"), out var policy);
                var trimMode = policy.NumOrNull("DefaultTrimMode");
                context.Write("lists", new Dictionary<string, object?>
                {
                    ["webUrl"] = web,
                    ["listTitle"] = list.Str("Title"),
                    ["listUrl"] = ListTypes.Url(web, list.Child("RootFolder").Str("ServerRelativeUrl")),
                    ["listType"] = ListTypes.Name(list.Num("BaseTemplate")),
                    ["baseTemplate"] = list.Num("BaseTemplate"),
                    ["itemCount"] = list.Num("ItemCount"),
                    ["hidden"] = hidden ? true : null,
                    ["created"] = list.When("Created"),
                    ["lastModified"] = list.When("LastItemModifiedDate"),
                    ["versioning"] = !versioning ? "None" : minor ? "Major and minor" : "Major",
                    ["majorVersionLimit"] = versioning ? list.Num("MajorVersionLimit") : null,
                    ["minorVersionLimit"] = versioning && minor ? list.Num("MajorWithMinorVersionsLimit") : null,
                    ["versionTrimming"] = trimMode switch { 0 => "By count", 1 => "By age", 2 => "Automatic", _ => null },
                    ["expireAfterDays"] = policy.NumOrNull("DefaultExpireAfterDays") is > 0 and var days ? days : null,
                    ["contentApproval"] = list.Bool("EnableModeration") ? true : null,
                    ["requireCheckout"] = list.Bool("ForceCheckout") ? true : null,
                    ["draftVisibility"] = versioning && minor ? list.Num("DraftVersionVisibility") switch { 0 => "Readers", 1 => "Authors", 2 => "Approvers", _ => null } : null,
                    ["listId"] = list.Str("Id"),
                });
                context.Count("lists");
                context.Count("items", list.Num("ItemCount"));
                if (list.Num("BaseTemplate") == 101) context.Count("libraries");
            }
        }
    }
}

/// <summary>Subsites at every level.</summary>
public sealed class SubsitesReport : ReportDefinition
{
    public override string Id => "subsites";
    public override string Title => "Subsites";
    public override string Group => "Discovery";
    public override string Summary => "Every subsite below each site, at all levels, with its template and dates.";

    public override IReadOnlyList<ReportTable> Tables => new[]
    {
        Table("subsites", "Subsites",
            Text("subsiteUrl"), Text("subsiteTitle"), Text("parentUrl"), Number("level"), Text("template"), Date("created"), Date("lastModified"), Text("language")),
    };

    public override async Task RunAsync(ReportContext context)
    {
        var subsites = await WebWalker.SubsitesAsync(context.Connection, context.Cancel);
        foreach (var web in subsites)
            context.Write("subsites", new Dictionary<string, object?>
            {
                ["subsiteUrl"] = web.Url,
                ["subsiteTitle"] = web.Title,
                ["parentUrl"] = web.ParentUrl,
                ["level"] = web.Level,
                ["template"] = web.Template,
                ["created"] = web.Created,
                ["lastModified"] = web.LastModified,
                ["language"] = TenantSites.Language(web.Language),
            });
        context.Count("subsites", subsites.Count);
        if (subsites.Count > 0) context.Count("sitesWithSubsites");
    }
}

/// <summary>Whether the classic publishing features are switched on.</summary>
public sealed class PublishingReport : ReportDefinition
{
    private static readonly Guid Infrastructure = new("f6924d36-2fa8-4f0b-b16d-06b7250180fa");
    private static readonly Guid PublishingWeb = new("94c94ca6-b32f-4da9-a9e3-1f3d343d7ecb");

    public override string Id => "publishing";
    public override string Title => "Publishing sites";
    public override string Group => "Discovery";
    public override string Summary => "Sites with the classic publishing features switched on, which need attention in a migration.";

    public override IReadOnlyList<ReportTable> Tables => new[]
    {
        Table("publishing", "Publishing", Text("siteTitle"), Flag("publishingInfrastructure"), Flag("publishingWeb"), Text("status")),
    };

    public override async Task RunAsync(ReportContext context)
    {
        var rest = context.Connection.Rest;
        var site = context.Connection.SiteUrl;
        var siteFeatures = (await rest.GetAllAsync($"{site}/_api/site/features?$select=DefinitionId", context.Cancel)).Select(f => f.Id("DefinitionId")).ToHashSet();
        var webFeatures = (await rest.GetAllAsync($"{site}/_api/web/features?$select=DefinitionId", context.Cancel)).Select(f => f.Id("DefinitionId")).ToHashSet();
        using var web = await rest.GetJsonAsync($"{site}/_api/web?$select=Title");
        var infrastructure = siteFeatures.Contains(Infrastructure);
        var publishingWeb = webFeatures.Contains(PublishingWeb);
        context.Write("publishing", new Dictionary<string, object?>
        {
            ["siteTitle"] = web.RootElement.Str("Title"),
            ["publishingInfrastructure"] = infrastructure,
            ["publishingWeb"] = publishingWeb,
            ["status"] = infrastructure && publishingWeb ? "Both" : infrastructure ? "Infrastructure only" : publishingWeb ? "Web only" : "None",
        });
        context.Count(infrastructure && publishingWeb ? "both" : infrastructure ? "infrastructureOnly" : publishingWeb ? "webOnly" : "none");
    }
}

/// <summary>External users on a site.</summary>
public sealed class GuestsReport : ReportDefinition
{
    public override string Id => "guests";
    public override string Title => "Guest users";
    public override string Group => "Permissions";
    public override string Summary => "External users on each site: guests who have an account in the directory, and people who sign in with a one-time passcode and have none.";

    public override IReadOnlyList<ReportOption> Options => new[]
    {
        new ReportOption("kind", "text", "All",
            "Which guests. All = both kinds. Directory = guests with an account in the directory (invited, B2B). OneTimePasscode = people who sign in with a code sent by email and have no account (ad hoc guests).",
            new[] { "All", "Directory", "OneTimePasscode" }),
    };

    public override IReadOnlyList<ReportTable> Tables => new[]
    {
        Table("guests", "Guests", Text("guestType"), Text("title"), Text("email"), Flag("siteAdmin"), Number("userId"), Text("loginName")),
    };

    public override async Task RunAsync(ReportContext context)
    {
        var kind = context.Options.Text("kind").ToLowerInvariant();
        // Filtering on the server is unreliable for sign-in names with encoded characters, so every site
        // user is read and the guests picked out here.
        int directory = 0, passcode = 0;
        await context.Connection.Rest.ForEachAsync(
            $"{context.Connection.SiteUrl}/_api/web/siteusers?$select=Id,LoginName,Title,Email,IsSiteAdmin&$top=2000", user =>
            {
                var login = user.Str("LoginName");
                // A one-time passcode guest has no directory account: its sign-in name says "guest" outright.
                // A directory guest has an account in this tenant whose name carries the mark #ext#.
                var isPasscode = login.Contains("spo:guest", StringComparison.OrdinalIgnoreCase) || login.Contains("spo%3aguest", StringComparison.OrdinalIgnoreCase);
                var isDirectory = !isPasscode && login.Contains("#ext#", StringComparison.OrdinalIgnoreCase);
                if (!isPasscode && !isDirectory) return;
                if ((kind == "directory" && !isDirectory) || (kind == "onetimepasscode" && !isPasscode)) return;
                context.Write("guests", new Dictionary<string, object?>
                {
                    ["guestType"] = isPasscode ? "One-time passcode" : "Directory guest",
                    ["title"] = user.Str("Title"),
                    ["email"] = user.Str("Email"),
                    ["siteAdmin"] = user.Bool("IsSiteAdmin") ? true : null,
                    ["userId"] = user.Num("Id"),
                    ["loginName"] = login,
                });
                if (isPasscode) passcode++; else directory++;
            }, context.Cancel);
        context.Count("guests", directory + passcode);
        context.Count("directoryGuests", directory);
        context.Count("oneTimePasscodeGuests", passcode);
        if (directory + passcode > 0) context.Count("sitesWithGuests");
    }
}

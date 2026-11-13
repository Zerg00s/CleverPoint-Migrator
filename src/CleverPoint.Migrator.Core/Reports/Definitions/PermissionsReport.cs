using System.Text.Json;
using CleverPoint.Migrator.Core.Csom;
using CleverPoint.Migrator.Core.Hosting;
using CleverPoint.Migrator.Core.Http;
using CleverPoint.Migrator.Core.Operations;

namespace CleverPoint.Migrator.Core.Reports.Definitions;

/// <summary>
/// Who has access to what: the site, and every list, folder, file and item that has its own
/// permissions instead of inheriting them. Plus the sharing links on those objects.
/// </summary>
public sealed class PermissionsReport : ReportDefinition
{
    public override string Id => "permissions";
    public override string Title => "Permissions";
    public override string Group => "Permissions";
    public override string Summary => "Who has access: the site's own permissions and every list, folder, file and item with unique permissions, plus sharing links (anyone, organization, specific people, expired).";

    public override IReadOnlyList<ReportOption> Options => new[]
    {
        new ReportOption("includeItems", "bool", true, "Look inside lists and libraries for folders, files and items with unique permissions. The slow part on large libraries."),
        new ReportOption("sharingLinks", "bool", true, "List the sharing links on objects with unique permissions."),
        new ReportOption("linkUrls", "bool", false, "Include the address of each sharing link. An \"anyone\" link opens the item for whoever has its address, so leave this off unless the report is kept safe."),
        new ReportOption("expandGroups", "bool", false, "Also list the members of each SharePoint group that has access."),
        new ReportOption("limitedAccess", "bool", false, "Also list the \"Limited Access\" levels, which SharePoint adds by itself so people can reach what was shared below."),
        new ReportOption("includeHidden", "bool", false, "Also look at hidden lists."),
        new ReportOption("includeSubsites", "bool", false, "Also scan every subsite of each site."),
    };

    public override IReadOnlyList<ReportTable> Tables => new[]
    {
        Table("permissions", "Permissions",
            Text("webUrl"), Text("objectType"), Text("objectTitle"), Text("objectUrl"), Text("principal"), Text("principalType"),
            Text("permissionLevels"), IfAny(Text("through")), Text("email"), Text("login")),
        Table("sharingLinks", "Sharing links",
            Text("webUrl"), Text("objectType"), Text("objectTitle"), Text("objectUrl"), Text("audience"), Text("access"), Flag("active"),
            Date("expiration"), Flag("expired"), Flag("requiresPassword"), Text("linkKind"), Text("shareId"), IfAny(Text("linkUrl"))),
    };

    private sealed record Securable(string Type, string Title, string Url);

    public override async Task RunAsync(ReportContext context)
    {
        var rest = context.Connection.Rest;
        var authority = new Uri(context.Connection.SiteUrl).GetLeftPart(UriPartial.Authority);
        var isFirst = true;
        foreach (var web in await WebWalker.WebsAsync(context, context.Options.Bool("includeSubsites")))
        {
            context.Phase(web);
            var groups = new Dictionary<long, List<JsonElement>>();      // members of SharePoint groups, read once per web

            // The web itself. A subsite that inherits has nothing of its own to report.
            using (var info = await rest.GetJsonAsync($"{web}/_api/web?$select=Title,HasUniqueRoleAssignments"))
            {
                if (isFirst || info.RootElement.Bool("HasUniqueRoleAssignments"))
                {
                    var site = new Securable(isFirst ? "Site" : "Subsite", info.RootElement.Str("Title"), web);
                    await WriteAssignmentsAsync(context, web, site, await rest.GetAllAsync($"{web}/_api/web/roleassignments?{Expand}", context.Cancel), groups);
                    context.Count("uniqueObjects");
                }
            }
            if (isFirst)
            {
                // Site collection administrators hold everything without appearing in any role assignment.
                foreach (var admin in await rest.GetAllAsync($"{web}/_api/web/siteusers?$filter=IsSiteAdmin eq true&$select=Title,LoginName,Email,PrincipalType", context.Cancel))
                    Write(context, web, new Securable("Site", "", web), admin.Str("Title"), PrincipalTypeName((int)admin.Num("PrincipalType"), admin.Str("LoginName")),
                        "Site collection administrator", null, admin.Str("Email"), admin.Str("LoginName"));
            }
            isFirst = false;

            var lists = await rest.GetAllAsync(
                $"{web}/_api/web/lists?$select=Id,Title,Hidden,BaseType,BaseTemplate,ItemCount,HasUniqueRoleAssignments,RootFolder/ServerRelativeUrl&$expand=RootFolder&$top=500",
                context.Cancel);
            foreach (var list in lists)
            {
                context.Cancel.ThrowIfCancellationRequested();
                if (list.Bool("Hidden") && !context.Options.Bool("includeHidden")) continue;
                try
                {
                var listId = list.Str("Id");
                var isLibrary = list.Num("BaseType") == 1;
                var root = list.Child("RootFolder").Str("ServerRelativeUrl");
                if (list.Bool("HasUniqueRoleAssignments"))
                {
                    var securable = new Securable(isLibrary ? "Library" : "List", list.Str("Title"), authority + root);
                    await WriteAssignmentsAsync(context, web, securable,
                        await rest.GetAllAsync($"{web}/_api/web/lists(guid'{listId}')/roleassignments?{Expand}", context.Cancel), groups);
                    context.Count("uniqueObjects");
                }
                context.Count("listsScanned");
                if (!context.Options.Bool("includeItems") || list.Num("ItemCount") == 0) continue;

                context.Phase($"{list.Str("Title")}, {list.Num("ItemCount"):N0} items");
                var unique = await UniqueItemsAsync(context, web, Guid.Parse(listId));
                for (var i = 0; i < unique.Count; i += RestBatch.MaxPerBatch)
                {
                    context.Cancel.ThrowIfCancellationRequested();
                    var slice = unique.Skip(i).Take(RestBatch.MaxPerBatch).ToList();
                    var urls = slice.Select(item => $"{web}/_api/web/lists(guid'{listId}')/items({item.Id})/roleassignments?{Expand}").ToList();
                    List<(int Status, string Body)> answers;
                    try { answers = await RestBatch.GetAsync(context.Connection, web, urls); }
                    catch (SpRestException) { answers = new(); }
                    for (var n = 0; n < slice.Count; n++)
                    {
                        var item = slice[n];
                        // A list item's "file name" is only its ID with a suffix; say what it is instead.
                        var name = item.IsFolder || isLibrary ? item.FileRef[(item.FileRef.LastIndexOf('/') + 1)..] : $"Item {item.Id}";
                        var securable = new Securable(item.IsFolder ? "Folder" : isLibrary ? "File" : "Item", name, authority + item.FileRef);
                        List<JsonElement> assignments;
                        if (n < answers.Count && answers[n].Status == 200)
                        {
                            using var parsed = JsonDocument.Parse(answers[n].Body);
                            assignments = parsed.RootElement.TryGetProperty("value", out var value) ? value.EnumerateArray().Select(v => v.Clone()).ToList() : new();
                        }
                        else
                        {
                            // The batch failed for this one: ask for it alone, so one bad item cannot hide the rest.
                            try { assignments = await rest.GetAllAsync(urls[n], context.Cancel); }
                            catch (SpRestException) { context.Count("unreadableObjects"); continue; }
                        }
                        await WriteAssignmentsAsync(context, web, securable, assignments, groups);
                        context.Count("uniqueObjects");
                        if (context.Options.Bool("sharingLinks")) await WriteLinksAsync(context, web, listId, item.Id, securable);
                    }
                }
                }
                catch (Exception ex) when (Gone.Is(ex))
                {
                    // The list was deleted while the site was being read. The rest of the site is still worth having.
                    context.Count(Gone.Counter);
                }
            }
        }
    }

    private const string Expand = "$expand=Member,RoleDefinitionBindings&$top=500";

    /// <summary>The folders, files and items of a list that have their own permissions.</summary>
    private static async Task<List<UniquePermissionScanner.ScannedItem>> UniqueItemsAsync(ReportContext context, string web, Guid listId)
    {
        using var csom = context.Connection.ForWeb(web).CreateContext();
        var list = csom.Web.Lists.GetById(listId);
        var unique = new List<UniquePermissionScanner.ScannedItem>();
        await UniquePermissionScanner.ScanAsync(list, csom, item => { if (item.Unique) unique.Add(item); }, context.Cancel);
        return unique;
    }

    private async Task WriteAssignmentsAsync(ReportContext context, string web, Securable securable, List<JsonElement> assignments,
        Dictionary<long, List<JsonElement>> groups)
    {
        var showLimited = context.Options.Bool("limitedAccess");
        foreach (var assignment in assignments)
        {
            var member = assignment.Child("Member");
            var levels = assignment.Child("RoleDefinitionBindings") is { ValueKind: JsonValueKind.Array } bindings
                ? bindings.EnumerateArray().Where(b => showLimited || (b.Num("RoleTypeKind") != 1 && !b.Bool("Hidden"))).Select(b => b.Str("Name")).Where(n => n.Length > 0).ToList()
                : new List<string>();
            if (levels.Count == 0) continue;
            var login = member.Str("LoginName");
            var type = PrincipalTypeName((int)member.Num("PrincipalType"), login);
            var permission = string.Join("; ", levels);
            Write(context, web, securable, member.Str("Title"), type, permission, null, member.Str("Email"), login);

            if (type != "SharePoint group" || !context.Options.Bool("expandGroups")) continue;
            var groupId = member.Num("Id");
            if (!groups.TryGetValue(groupId, out var members))
            {
                try
                {
                    members = await context.Connection.Rest.GetAllAsync(
                        $"{web}/_api/web/sitegroups({groupId})/users?$select=Title,LoginName,Email,PrincipalType&$top=2000", context.Cancel);
                }
                catch (SpRestException)
                {
                    members = new();      // a group whose membership is hidden from this account
                }
                groups[groupId] = members;
            }
            foreach (var user in members)
                Write(context, web, securable, user.Str("Title"), PrincipalTypeName((int)user.Num("PrincipalType"), user.Str("LoginName")),
                    permission, member.Str("Title"), user.Str("Email"), user.Str("LoginName"));
        }
    }

    private static void Write(ReportContext context, string web, Securable securable, string principal, string type, string levels,
        string? through, string email, string login)
    {
        context.Write("permissions", new Dictionary<string, object?>
        {
            ["webUrl"] = web,
            ["objectType"] = securable.Type,
            ["objectTitle"] = securable.Title,
            ["objectUrl"] = securable.Url,
            ["principal"] = principal,
            ["principalType"] = type,
            ["permissionLevels"] = levels,
            ["through"] = through,
            ["email"] = email,
            ["login"] = login,
        });
        context.Count("permissions");
    }

    /// <summary>A principal's kind in words. A sharing link shows up as a group named after the link.</summary>
    private static string PrincipalTypeName(int principalType, string login)
    {
        if (login.StartsWith("SharingLinks.", StringComparison.OrdinalIgnoreCase)) return "Sharing link";
        if (login.Contains("spo-grid-all-users", StringComparison.OrdinalIgnoreCase)) return "Everyone except external users";
        if (login.Equals("c:0(.s|true", StringComparison.OrdinalIgnoreCase)) return "Everyone";
        return SpPrincipal.Classify(principalType, login, fromSiteGroups: false) switch
        {
            PrincipalKind.SharePointGroup => "SharePoint group",
            PrincipalKind.M365Group => "Microsoft 365 group",
            PrincipalKind.SecurityGroup => "Security group",
            _ => login.Contains("#ext#", StringComparison.OrdinalIgnoreCase) || login.Contains("spo:guest", StringComparison.OrdinalIgnoreCase)
                 || login.Contains("spo%3aguest", StringComparison.OrdinalIgnoreCase) ? "Guest" : "User",
        };
    }

    private static async Task WriteLinksAsync(ReportContext context, string web, string listId, int itemId, Securable securable)
    {
        JsonElement links;
        try
        {
            var response = await context.Connection.Rest.PostAsync(
                $"{web}/_api/web/lists(guid'{listId}')/GetItemById({itemId})/GetSharingInformation?$Expand=permissionsInformation",
                new { request = new { populateInheritedLinks = true } });
            using var parsed = JsonDocument.Parse(response);
            if (!TryGet(parsed.RootElement, "permissionsInformation", out var information) || !TryGet(information, "links", out links)) return;
            if (links.ValueKind == JsonValueKind.Object && TryGet(links, "results", out var inner)) links = inner;
            if (links.ValueKind != JsonValueKind.Array) return;
            links = links.Clone();
        }
        catch (Exception ex) when (ex is SpRestException or JsonException)
        {
            // Sharing details are an extra. An object that will not give them still has its permissions listed.
            context.Count("linksUnreadable");
            return;
        }

        foreach (var entry in links.EnumerateArray())
        {
            var link = TryGet(entry, "linkDetails", out var details) ? details : entry;
            var shareId = Text(link, "shareId");
            if (shareId.Length == 0 || shareId == Guid.Empty.ToString()) continue;      // a slot for a kind of link that was never created
            var kind = (int)Number(link, "linkKind");
            var anonymous = Flag(link, "allowsAnonymousAccess") || kind is 4 or 5;
            var scope = (int)Number(link, "scope");
            var audience = anonymous ? "Anyone" : kind is 2 or 3 || scope == 1 ? "Organization" : "Specific people";
            DateTime? expiration = DateTime.TryParse(Text(link, "expiration"), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var date) && date.Year > 1900 ? date : null;
            var expired = expiration is { } until && until < DateTime.UtcNow;
            var active = Flag(link, "isActive");
            context.Write("sharingLinks", new Dictionary<string, object?>
            {
                ["webUrl"] = web,
                ["objectType"] = securable.Type,
                ["objectTitle"] = securable.Title,
                ["objectUrl"] = securable.Url,
                ["audience"] = audience,
                ["access"] = Flag(link, "isEditLink") || kind is 3 or 5 ? "Edit" : "View",
                ["active"] = active ? true : null,
                ["expiration"] = expiration,
                ["expired"] = expired ? true : null,
                ["requiresPassword"] = Flag(link, "requiresPassword") ? true : null,
                ["linkKind"] = kind switch { 1 => "Direct", 2 => "Organization view", 3 => "Organization edit", 4 => "Anonymous view", 5 => "Anonymous edit", 6 => "Flexible", _ => kind.ToString() },
                ["shareId"] = shareId,
                ["linkUrl"] = context.Options.Bool("linkUrls") ? Text(link, "url") : null,
            });
            context.Count("sharingLinks");
            context.Count(audience switch { "Anyone" => "anyoneLinks", "Organization" => "organizationLinks", _ => "specificPeopleLinks" });
            if (expired) context.Count("expiredLinks");
        }
    }

    // The sharing API answers with property names in a different case depending on the format, so they are looked up loosely.
    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
        value = default;
        return false;
    }

    private static string Text(JsonElement element, string name) =>
        TryGet(element, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static bool Flag(JsonElement element, string name) => TryGet(element, name, out var value) && value.ValueKind == JsonValueKind.True;

    private static double Number(JsonElement element, string name) =>
        TryGet(element, name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;
}

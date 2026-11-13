using System.Text.Json;
using CleverPoint.Migrator.Core.Csom;
using CleverPoint.Migrator.Core.Hosting;
using CleverPoint.Migrator.Core.Http;

namespace CleverPoint.Migrator.Core.Reports.Definitions;

/// <summary>Source site against target site: the links in the navigation.</summary>
public sealed class NavigationCompareReport : ReportDefinition
{
    public override string Id => "navigation-compare";
    public override string Title => "Navigation compare";
    public override string Group => "Compare";
    public override string Summary => "Source site against target site: every link of the quick launch and the top navigation, with the ones that are missing, extra or point somewhere else on the target.";
    public override ReportScope Scope => ReportScope.SitePairs;

    public override IReadOnlyList<ReportOption> Options => new[]
    {
        new ReportOption("includeMatches", "bool", true, "Also list the links that are the same on both sides."),
    };

    public override IReadOnlyList<ReportTable> Tables => new[]
    {
        Table("links", "Links",
            Text("navigation"), Text("path"), Number("level"), Text("status"), Text("sourceLink"), Text("targetLink")),
    };

    private sealed record Node(string Path, int Level, string Link);

    public override async Task RunAsync(ReportContext context)
    {
        var target = context.Target ?? throw new InvalidOperationException("This report needs a target site.");
        var includeMatches = context.Options.Bool("includeMatches");
        foreach (var (name, endpoint) in new[] { ("Quick launch", "quicklaunch"), ("Top navigation", "topnavigationbar") })
        {
            var source = await NodesAsync(context.Connection, endpoint, context.Cancel);
            var there = await NodesAsync(target, endpoint, context.Cancel);
            // Two links can carry the same name in the same place; they are paired in order.
            var left = there.GroupBy(n => n.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => new Queue<Node>(g), StringComparer.OrdinalIgnoreCase);
            foreach (var node in source)
            {
                context.Count("links");
                if (left.TryGetValue(node.Path, out var same) && same.Count > 0)
                {
                    var other = same.Dequeue();
                    var equal = string.Equals(node.Link, other.Link, StringComparison.OrdinalIgnoreCase);
                    context.Count(equal ? "match" : "differentLink");
                    if (!equal || includeMatches) Write(context, name, node, equal ? "Match" : "Different link", node.Link, other.Link);
                }
                else
                {
                    context.Count("sourceOnly");
                    Write(context, name, node, "Source only", node.Link, null);
                }
            }
            foreach (var extra in left.Values.SelectMany(q => q))
            {
                context.Count("targetOnly");
                Write(context, name, extra, "Target only", null, extra.Link);
            }
        }
    }

    private static void Write(ReportContext context, string navigation, Node node, string status, string? sourceLink, string? targetLink) =>
        context.Write("links", new Dictionary<string, object?>
        {
            ["navigation"] = navigation, ["path"] = node.Path, ["level"] = node.Level, ["status"] = status, ["sourceLink"] = sourceLink, ["targetLink"] = targetLink,
        });

    /// <summary>Every link of one navigation, parents before children, each with the names leading to it.</summary>
    private static async Task<List<Node>> NodesAsync(SpConnection connection, string endpoint, CancellationToken ct)
    {
        var web = connection.SiteUrl;
        var nodes = new List<Node>();
        const string select = "$select=Id,Title,Url,IsVisible";

        async Task AddAsync(List<JsonElement> level, string parentPath, int depth)
        {
            foreach (var item in level)
            {
                var title = item.Str("Title").Trim();
                var path = parentPath.Length == 0 ? title : parentPath + " > " + title;
                nodes.Add(new Node(path, depth, Link(item.Str("Url"), web)));
                if (depth >= 4) continue;       // deeper than SharePoint's own menus show
                List<JsonElement> children;
                try { children = await connection.Rest.GetAllAsync($"{web}/_api/web/navigation/GetNodeById({item.Num("Id")})/Children?{select}", ct); }
                catch (SpRestException) { continue; }
                if (children.Count > 0) await AddAsync(children, path, depth + 1);
            }
        }

        await AddAsync(await connection.Rest.GetAllAsync($"{web}/_api/web/navigation/{endpoint}?{select}", ct), "", 1);
        return nodes;
    }

    /// <summary>
    /// A link in a form that compares across sites: one that points inside its own site is written
    /// relative to it ("~/Shared Documents"), so the same link on the target counts as the same.
    /// </summary>
    private static string Link(string url, string web)
    {
        if (url.Length == 0) return "";
        var site = new Uri(web);
        var absolute = url.StartsWith('/') ? site.GetLeftPart(UriPartial.Authority) + url : url;
        var decoded = Uri.UnescapeDataString(absolute).TrimEnd('/');
        var own = Uri.UnescapeDataString(web).TrimEnd('/');
        if (decoded.Equals(own, StringComparison.OrdinalIgnoreCase)) return "~";
        return decoded.StartsWith(own + "/", StringComparison.OrdinalIgnoreCase) ? "~" + decoded[own.Length..] : decoded;
    }
}

/// <summary>Source site against target site: who has which permissions on the site and on its lists.</summary>
public sealed class PermissionsCompareReport : ReportDefinition
{
    public override string Id => "permissions-compare";
    public override string Title => "Permissions compare";
    public override string Group => "Compare";
    public override string Summary => "Source site against target site: who has which permission level on the site and on each list with unique permissions, and where the two differ. Folders, files and items are not compared.";
    public override ReportScope Scope => ReportScope.SitePairs;

    public override IReadOnlyList<ReportOption> Options => new[]
    {
        new ReportOption("includeMatches", "bool", false, "Also list the permissions that are the same on both sides."),
        new ReportOption("includeLists", "bool", true, "Compare lists and libraries as well as the site itself."),
        new ReportOption("includeHidden", "bool", false, "Also compare hidden lists."),
    };

    public override IReadOnlyList<ReportTable> Tables => new[]
    {
        Table("differences", "Permissions",
            Text("objectType"), Text("object"), Text("status"), Text("principal"), Text("principalType"), Text("sourceLevels"), Text("targetLevels"),
            Text("sourceUrl"), Text("targetUrl")),
    };

    private sealed record Grant(string Key, string Name, string Type, SortedSet<string> Levels);

    private const string Expand = "$expand=Member,RoleDefinitionBindings&$top=500";

    public override async Task RunAsync(ReportContext context)
    {
        var target = context.Target ?? throw new InvalidOperationException("This report needs a target site.");
        var source = context.Connection;
        var sourceRoles = await SiteGroupsAsync(source);
        var targetRoles = await SiteGroupsAsync(target);

        Compare(context, "Site", "", source.SiteUrl, target.SiteUrl,
            Grants(await source.Rest.GetAllAsync($"{source.SiteUrl}/_api/web/roleassignments?{Expand}", context.Cancel), sourceRoles),
            Grants(await target.Rest.GetAllAsync($"{target.SiteUrl}/_api/web/roleassignments?{Expand}", context.Cancel), targetRoles));
        if (!context.Options.Bool("includeLists")) return;

        var hidden = context.Options.Bool("includeHidden");
        var excluded = new HashSet<string>(ListPairing.SystemLists, StringComparer.OrdinalIgnoreCase);
        var left = (await ListPairing.ListsAsync(source, hidden, context.Cancel)).Where(l => !excluded.Contains(l.Title)).ToList();
        var right = (await ListPairing.ListsAsync(target, hidden, context.Cancel)).Where(l => !excluded.Contains(l.Title)).ToList();
        var sourceAuthority = new Uri(source.SiteUrl).GetLeftPart(UriPartial.Authority);
        var targetAuthority = new Uri(target.SiteUrl).GetLeftPart(UriPartial.Authority);
        var sourceUnique = await UniqueListsAsync(source, context.Cancel);
        var targetUnique = await UniqueListsAsync(target, context.Cancel);

        foreach (var (a, b) in ListPairing.Pair(left, right))
        {
            context.Cancel.ThrowIfCancellationRequested();
            // A list on one side only is a finding for the list compare, not a permissions difference.
            if (a is null || b is null) continue;
            bool aUnique = sourceUnique.Contains(a.Id), bUnique = targetUnique.Contains(b.Id);
            if (!aUnique && !bUnique) continue;
            var type = a.IsLibrary ? "Library" : "List";
            var sourceUrl = sourceAuthority + a.RootFolder;
            var targetUrl = targetAuthority + b.RootFolder;
            if (aUnique != bUnique)
            {
                context.Count("objectsCompared");
                context.Count("inheritanceDiffers");
                context.Write("differences", new Dictionary<string, object?>
                {
                    ["objectType"] = type, ["object"] = a.Title, ["status"] = "Inheritance differs",
                    ["sourceLevels"] = aUnique ? "Unique permissions" : "Inherits from the site",
                    ["targetLevels"] = bUnique ? "Unique permissions" : "Inherits from the site",
                    ["sourceUrl"] = sourceUrl, ["targetUrl"] = targetUrl,
                });
                continue;
            }
            try
            {
                Compare(context, type, a.Title, sourceUrl, targetUrl,
                    Grants(await source.Rest.GetAllAsync($"{source.SiteUrl}/_api/web/lists(guid'{a.Id}')/roleassignments?{Expand}", context.Cancel), sourceRoles),
                    Grants(await target.Rest.GetAllAsync($"{target.SiteUrl}/_api/web/lists(guid'{b.Id}')/roleassignments?{Expand}", context.Cancel), targetRoles));
            }
            catch (Exception ex) when (Gone.Is(ex)) { context.Count(Gone.Counter); }
        }
    }

    private static void Compare(ReportContext context, string type, string title, string sourceUrl, string targetUrl, List<Grant> source, List<Grant> target)
    {
        context.Count("objectsCompared");
        var includeMatches = context.Options.Bool("includeMatches");
        var there = target.ToDictionary(g => g.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var grant in source)
        {
            string status;
            Grant? other = null;
            if (!there.Remove(grant.Key, out other)) status = "Source only";
            else status = grant.Levels.SetEquals(other.Levels) ? "Match" : "Different level";
            context.Count(status switch { "Match" => "match", "Different level" => "differentLevel", _ => "sourceOnly" });
            if (status == "Match" && !includeMatches) continue;
            Write(context, type, title, status, grant, other, sourceUrl, targetUrl);
        }
        foreach (var extra in there.Values)
        {
            context.Count("targetOnly");
            Write(context, type, title, "Target only", null, extra, sourceUrl, targetUrl);
        }
    }

    private static void Write(ReportContext context, string type, string title, string status, Grant? source, Grant? target, string sourceUrl, string targetUrl) =>
        context.Write("differences", new Dictionary<string, object?>
        {
            ["objectType"] = type,
            ["object"] = title,
            ["status"] = status,
            ["principal"] = (source ?? target)!.Name,
            ["principalType"] = (source ?? target)!.Type,
            ["sourceLevels"] = source is null ? null : string.Join("; ", source.Levels),
            ["targetLevels"] = target is null ? null : string.Join("; ", target.Levels),
            ["sourceUrl"] = sourceUrl,
            ["targetUrl"] = targetUrl,
        });

    /// <summary>The IDs of a site's own owners, members and visitors groups, which are named after the site and so differ between two sites.</summary>
    private static async Task<Dictionary<long, string>> SiteGroupsAsync(SpConnection connection)
    {
        var roles = new Dictionary<long, string>();
        try
        {
            using var web = await connection.Rest.GetJsonAsync(
                $"{connection.SiteUrl}/_api/web?$select=Id&$expand=AssociatedOwnerGroup,AssociatedMemberGroup,AssociatedVisitorGroup");
            foreach (var (property, role) in new[] { ("AssociatedOwnerGroup", "Site owners group"), ("AssociatedMemberGroup", "Site members group"), ("AssociatedVisitorGroup", "Site visitors group") })
                if (web.RootElement.Child(property) is { ValueKind: JsonValueKind.Object } group && group.Num("Id") > 0)
                    roles[group.Num("Id")] = role;
        }
        catch (SpRestException) { /* a site without them: groups are then matched by name like any other */ }
        return roles;
    }

    private static async Task<HashSet<string>> UniqueListsAsync(SpConnection connection, CancellationToken ct) =>
        (await connection.Rest.GetAllAsync($"{connection.SiteUrl}/_api/web/lists?$select=Id,HasUniqueRoleAssignments&$top=500", ct))
        .Where(l => l.Bool("HasUniqueRoleAssignments")).Select(l => l.Str("Id")).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Who has what, in a form that compares across sites and tenants: a person by email, the site's own
    /// groups by their role, any other group by name, and a permission level by its kind where it is
    /// one of the built-in ones (so "Read" and "Lesen" are the same level).
    /// </summary>
    private static List<Grant> Grants(List<JsonElement> assignments, Dictionary<long, string> siteGroups)
    {
        var grants = new Dictionary<string, Grant>(StringComparer.OrdinalIgnoreCase);
        foreach (var assignment in assignments)
        {
            var member = assignment.Child("Member");
            var levels = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            if (assignment.Child("RoleDefinitionBindings") is { ValueKind: JsonValueKind.Array } bindings)
                foreach (var binding in bindings.EnumerateArray())
                {
                    if (binding.Bool("Hidden") || binding.Num("RoleTypeKind") == 1) continue;       // the Limited Access levels
                    levels.Add(binding.Num("RoleTypeKind") switch
                    {
                        2 => "Read", 3 => "Contribute", 4 => "Design", 5 => "Full Control", 6 => "Edit",
                        _ => binding.Str("Name"),
                    });
                }
            if (levels.Count == 0) continue;
            var login = member.Str("LoginName");
            // Sharing links belong to single files and never line up between two sites.
            if (login.StartsWith("SharingLinks.", StringComparison.OrdinalIgnoreCase)) continue;
            var kind = SpPrincipal.Classify((int)member.Num("PrincipalType"), login, fromSiteGroups: false);
            string key, name, type;
            if (siteGroups.TryGetValue(member.Num("Id"), out var role))
            {
                (key, name, type) = (role, $"{role} ({member.Str("Title")})", "SharePoint group");
            }
            else if (kind == PrincipalKind.User)
            {
                var email = member.Str("Email");
                (key, name, type) = (email.Length > 0 ? email : member.Str("Title"), member.Str("Title") + (email.Length > 0 ? $" <{email}>" : ""), "User");
            }
            else
            {
                (key, name, type) = (member.Str("Title"), member.Str("Title"), kind switch
                {
                    PrincipalKind.SharePointGroup => "SharePoint group",
                    PrincipalKind.M365Group => "Microsoft 365 group",
                    _ => "Security group",
                });
            }
            if (grants.TryGetValue(key, out var existing)) existing.Levels.UnionWith(levels);
            else grants[key] = new Grant(key, name, type, levels);
        }
        return grants.Values.ToList();
    }
}

using CleverPoint.Migrator.Core.Csom;
using CleverPoint.Migrator.Core.Model;
using Microsoft.SharePoint.Client;
using Microsoft.SharePoint.Client.Utilities;

namespace CleverPoint.Migrator.Core.Operations;

/// <summary>
/// Copies UNIQUE role assignments (broken inheritance) for lists, items, files and
/// folders, behind CopyOptions.CopyPermissions and the permissions-only mode.
/// Users map through the UserResolver (manual mapping + fallback apply).
/// SharePoint groups match through the mapping, then by name, then by role (the
/// source site's Owners/Members/Visitors group to the target site's). Permission
/// levels match by name, then by kind, so "Lesen" on a German site finds "Read"
/// on an English one. Unresolvable principals are recorded as warnings.
/// </summary>
public class PermissionCopier
{
    private readonly ClientContext _sourceCtx;
    private readonly ClientContext _targetCtx;
    private readonly UserResolver _users;
    private readonly Dictionary<string, string> _groupMap;
    private Dictionary<string, Group>? _targetGroupsByName;
    private Dictionary<string, string>? _associatedGroupMap;
    private List<RoleDefinition>? _targetRoleDefs;

    public PermissionCopier(ClientContext sourceCtx, ClientContext targetCtx, UserResolver users,
        Dictionary<string, string>? groupMap = null)
    {
        _sourceCtx = sourceCtx;
        _targetCtx = targetCtx;
        _users = users;
        _groupMap = groupMap ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Source item ids with unique permissions (cheap bulk probe, paged).</summary>
    public async Task<HashSet<int>> FindUniquePermissionItemsAsync(List sourceList, int pageSize = 200)
    {
        var unique = new HashSet<int>();
        var query = new CamlQuery { ViewXml = $"<View Scope='RecursiveAll'><RowLimit Paged='TRUE'>{pageSize}</RowLimit></View>" };
        do
        {
            var page = sourceList.GetItems(query);
            _sourceCtx.Load(page, p => p.Include(i => i.Id, i => i.HasUniqueRoleAssignments), p => p.ListItemCollectionPosition);
            await _sourceCtx.ExecuteWithRetryAsync();
            foreach (var item in page)
                if (item.HasUniqueRoleAssignments)
                    unique.Add(item.Id);
            query.ListItemCollectionPosition = page.ListItemCollectionPosition;
        } while (query.ListItemCollectionPosition != null);
        return unique;
    }

    /// <summary>Copies a single item's unique role assignments onto its target item, next to whatever the target has.</summary>
    public Task CopyItemPermissionsAsync(ListItem sourceItem, ListItem targetItem, string sourceRef, CopyResult result) =>
        CopyAsync(sourceItem, targetItem, sourceRef, "", result, replaceExisting: false);

    /// <summary>The target context, for callers that pair source and target objects themselves.</summary>
    internal ClientContext TargetContext => _targetCtx;
    internal ClientContext SourceContext => _sourceCtx;

    /// <summary>
    /// Copies the unique role assignments of a list, folder, file or item onto its target.
    ///
    /// Everything is resolved before the target is touched. When nothing can be mapped, the target is
    /// left exactly as it was, because breaking inheritance and granting nobody would lock the item.
    /// </summary>
    /// <param name="replaceExisting">
    /// True makes the target match the source: assignments the target already has are removed, so it ends
    /// up with the source's and nothing else. False only adds.
    /// </param>
    /// <param name="outcomeType">
    /// Record type of the one line that says what happened to this object. "Permission" by default; the
    /// permissions-only mode passes "File", "Folder" or "Item" so the line counts toward run progress.
    /// </param>
    /// <returns>True when assignments were applied.</returns>
    public async Task<bool> CopyAsync(SecurableObject source, SecurableObject target, string sourceRef, string targetRef,
        CopyResult result, bool replaceExisting, string outcomeType = "Permission")
    {
        var assignments = source.RoleAssignments;
        _sourceCtx.Load(assignments, ras => ras.Include(
            ra => ra.Member.LoginName, ra => ra.Member.Title, ra => ra.Member.PrincipalType,
            ra => ra.RoleDefinitionBindings.Include(rd => rd.Name, rd => rd.RoleTypeKind)));
        await _sourceCtx.ExecuteWithRetryAsync();

        var targetRoleDefs = await TargetRoleDefinitionsAsync();

        // 1. Resolve every principal and permission level. Nothing on the target changes yet.
        var planned = new List<(Principal Principal, int PrincipalId, List<RoleDefinition> Roles)>();
        var skipped = 0;
        foreach (var ra in assignments.AsEnumerable())
        {
            var roles = new List<RoleDefinition>();
            foreach (var rd in ra.RoleDefinitionBindings.AsEnumerable())
            {
                // "Limited Access" is granted by SharePoint itself and cannot be assigned.
                if (rd.RoleTypeKind == RoleType.Guest || rd.Name == "Limited Access") continue;
                var match = targetRoleDefs.FirstOrDefault(t => string.Equals(t.Name, rd.Name, StringComparison.OrdinalIgnoreCase))
                    ?? (rd.RoleTypeKind != RoleType.None ? targetRoleDefs.FirstOrDefault(t => t.RoleTypeKind == rd.RoleTypeKind) : null);
                if (match != null) roles.Add(match);
                else
                    result.Add("Permission", sourceRef, targetRef, ItemCopyStatus.Warning,
                        $"permission level '{rd.Name}' does not exist on the target; not granted to '{ra.Member.Title}'");
            }
            if (roles.Count == 0) continue;

            Principal? principal;
            int principalId;
            if (ra.Member.PrincipalType == PrincipalType.SharePointGroup)
            {
                var group = await ResolveGroupAsync(ra.Member.Title);
                principal = group;
                principalId = group?.Id ?? 0;
                if (principal == null)
                {
                    result.Add("Permission", sourceRef, targetRef, ItemCopyStatus.Warning,
                        $"group '{ra.Member.Title}' not found on target; assignment skipped");
                    skipped++;
                    continue;
                }
            }
            else
            {
                var userId = await _users.ResolveByLoginAsync(ra.Member.LoginName);
                if (!userId.HasValue)
                {
                    result.Add("Permission", sourceRef, targetRef, ItemCopyStatus.Warning,
                        $"principal '{ra.Member.Title}' unresolved; assignment skipped");
                    skipped++;
                    continue;
                }
                principal = _targetCtx.Web.GetUserById(userId.Value);
                principalId = userId.Value;
            }
            // Two source principals can map to one target account; their levels add up.
            var same = planned.FindIndex(p => p.PrincipalId == principalId);
            if (same >= 0) planned[same].Roles.AddRange(roles.Where(r => !planned[same].Roles.Contains(r)));
            else planned.Add((principal, principalId, roles));
        }

        if (planned.Count == 0)
        {
            result.Add(outcomeType, sourceRef, targetRef, ItemCopyStatus.Warning,
                "no source principals could be mapped to the target; target permissions left unchanged");
            return false;
        }

        // 2. Apply. Breaking inheritance without copying the parent's assignments leaves only the
        //    account running the copy, which SharePoint adds by itself.
        target.BreakRoleInheritance(false, false);
        var existing = new Dictionary<int, RoleAssignment>();
        if (replaceExisting)
        {
            var current = target.RoleAssignments;
            _targetCtx.Load(current, ras => ras.Include(ra => ra.PrincipalId));
            await _targetCtx.ExecuteWithRetryAsync();
            foreach (var ra in current.AsEnumerable()) existing[ra.PrincipalId] = ra;
        }
        foreach (var (principal, principalId, roles) in planned)
        {
            var bindings = new RoleDefinitionBindingCollection(_targetCtx);
            foreach (var role in roles) bindings.Add(role);
            if (existing.TryGetValue(principalId, out var have))
            {
                // Already assigned: set its levels to exactly the source's.
                have.ImportRoleDefinitionBindings(bindings);
                have.Update();
            }
            else
            {
                target.RoleAssignments.Add(principal, bindings);
            }
        }
        await _targetCtx.ExecuteWithRetryAsync();

        // 3. Only now remove what the source does not have. The new assignments are already in
        //    place, so the item is never left with nobody on it. The account running the copy goes
        //    last: removing it first could take away the right to remove the others.
        var removed = 0;
        if (replaceExisting)
        {
            var keep = planned.Select(p => p.PrincipalId).ToHashSet();
            var me = await CurrentTargetUserIdAsync();
            var stale = existing.Where(e => !keep.Contains(e.Key)).OrderBy(e => e.Key == me ? 1 : 0).Select(e => e.Value).ToList();
            if (stale.Count > 0)
            {
                foreach (var old in stale) old.DeleteObject();
                try
                {
                    await _targetCtx.ExecuteWithRetryAsync();
                    removed = stale.Count;
                }
                catch (ServerException ex)
                {
                    result.Add("Permission", sourceRef, targetRef, ItemCopyStatus.Warning,
                        $"the source's assignments were applied, but assignments the target already had could not all be removed: {ex.Message}");
                }
            }
        }

        result.Add(outcomeType, sourceRef, targetRef, ItemCopyStatus.Copied,
            $"{planned.Count} role assignment(s)" + (skipped > 0 ? $", {skipped} skipped" : "") +
            (removed > 0 ? $", removed {removed} the source does not have" : ""));
        return true;
    }

    private int? _currentTargetUserId;

    private async Task<int> CurrentTargetUserIdAsync()
    {
        if (_currentTargetUserId is { } known) return known;
        try
        {
            var user = _targetCtx.Web.CurrentUser;
            _targetCtx.Load(user, u => u.Id);
            await _targetCtx.ExecuteWithRetryAsync();
            _currentTargetUserId = user.Id;
        }
        catch (ServerException)
        {
            _currentTargetUserId = -1;
        }
        return _currentTargetUserId.Value;
    }

    private async Task<List<RoleDefinition>> TargetRoleDefinitionsAsync()
    {
        if (_targetRoleDefs != null) return _targetRoleDefs;
        var roleDefs = _targetCtx.Web.RoleDefinitions;
        _targetCtx.Load(roleDefs, rds => rds.Include(rd => rd.Name, rd => rd.RoleTypeKind));
        await _targetCtx.ExecuteWithRetryAsync();
        return _targetRoleDefs = roleDefs.AsEnumerable().ToList();
    }

    private async Task<Group?> ResolveGroupAsync(string groupName)
    {
        if (_targetGroupsByName == null)
        {
            var groups = _targetCtx.Web.SiteGroups;
            _targetCtx.Load(groups, gs => gs.Include(g => g.Id, g => g.Title));
            await _targetCtx.ExecuteWithRetryAsync();
            _targetGroupsByName = groups.AsEnumerable()
                .GroupBy(g => g.Title, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        }

        // 1. The mapping CSV may rename groups across tenants.
        if (_groupMap.TryGetValue(groupName, out var mapped) && _targetGroupsByName.TryGetValue(mapped, out var byMap)) return byMap;
        // 2. Same name on the target.
        if (_targetGroupsByName.TryGetValue(groupName, out var sameName)) return sameName;
        // 3. Same role: default groups are named after their site ("Sales Members"), so across
        //    sites the names differ while the role (owners, members, visitors) is the same.
        var byRole = await AssociatedGroupMapAsync();
        return byRole.TryGetValue(groupName, out var equivalent) ? _targetGroupsByName.GetValueOrDefault(equivalent) : null;
    }

    /// <summary>Source Owners/Members/Visitors group title to the target site's group in the same role.</summary>
    private async Task<Dictionary<string, string>> AssociatedGroupMapAsync()
    {
        if (_associatedGroupMap != null) return _associatedGroupMap;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var s = _sourceCtx.Web;
            var t = _targetCtx.Web;
            _sourceCtx.Load(s, w => w.AssociatedOwnerGroup.Title, w => w.AssociatedMemberGroup.Title, w => w.AssociatedVisitorGroup.Title);
            _targetCtx.Load(t, w => w.AssociatedOwnerGroup.Title, w => w.AssociatedMemberGroup.Title, w => w.AssociatedVisitorGroup.Title);
            await _sourceCtx.ExecuteWithRetryAsync();
            await _targetCtx.ExecuteWithRetryAsync();
            void Pair(Group source, Group target)
            {
                if (source.ServerObjectIsNull == false && target.ServerObjectIsNull == false) map[source.Title] = target.Title;
            }
            Pair(s.AssociatedOwnerGroup, t.AssociatedOwnerGroup);
            Pair(s.AssociatedMemberGroup, t.AssociatedMemberGroup);
            Pair(s.AssociatedVisitorGroup, t.AssociatedVisitorGroup);
        }
        catch (ServerException) { /* a site without default groups simply has no role pairs */ }
        return _associatedGroupMap = map;
    }
}

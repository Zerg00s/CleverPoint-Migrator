using CleverPoint.Migrator.Core.Model;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.Core.Operations;

/// <summary>
/// Copies UNIQUE role assignments (broken inheritance) for items, files and
/// folders, behind CopyOptions.CopyPermissions. Users map through the
/// UserResolver (manual mapping + fallback apply); SharePoint groups match
/// by name on the target; role definitions match by name. Unresolvable
/// principals are recorded as warnings, optionally replaced by the fallback.
/// </summary>
public class PermissionCopier
{
    private readonly ClientContext _sourceCtx;
    private readonly ClientContext _targetCtx;
    private readonly UserResolver _users;
    private readonly Dictionary<string, string> _groupMap;
    private Dictionary<string, Group>? _targetGroupsByName;

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
            await _sourceCtx.ExecuteQueryAsync();
            foreach (var item in page)
                if (item.HasUniqueRoleAssignments)
                    unique.Add(item.Id);
            query.ListItemCollectionPosition = page.ListItemCollectionPosition;
        } while (query.ListItemCollectionPosition != null);
        return unique;
    }

    /// <summary>Copies a single item's unique role assignments onto its target item.</summary>
    public async Task CopyItemPermissionsAsync(ListItem sourceItem, ListItem targetItem, string sourceRef, CopyResult result)
    {
        var assignments = sourceItem.RoleAssignments;
        _sourceCtx.Load(assignments, ras => ras.Include(
            ra => ra.Member.LoginName, ra => ra.Member.Title, ra => ra.Member.PrincipalType,
            ra => ra.RoleDefinitionBindings.Include(rd => rd.Name)));
        await _sourceCtx.ExecuteQueryAsync();

        targetItem.BreakRoleInheritance(false, false);
        await _targetCtx.ExecuteQueryAsync();

        var roleDefs = _targetCtx.Web.RoleDefinitions;
        _targetCtx.Load(roleDefs, rds => rds.Include(rd => rd.Name));
        await _targetCtx.ExecuteQueryAsync();
        var targetRoleDefs = roleDefs.AsEnumerable().ToDictionary(rd => rd.Name, StringComparer.OrdinalIgnoreCase);

        var applied = 0;
        foreach (var ra in assignments.AsEnumerable())
        {
            Principal? principal = null;
            if (ra.Member.PrincipalType == Microsoft.SharePoint.Client.Utilities.PrincipalType.SharePointGroup)
            {
                principal = await ResolveGroupAsync(ra.Member.Title);
                if (principal == null)
                {
                    result.Add("Permission", sourceRef, "", ItemCopyStatus.Warning,
                        $"group '{ra.Member.Title}' not found on target; assignment skipped");
                    continue;
                }
            }
            else
            {
                var userId = await _users.ResolveByLoginAsync(ra.Member.LoginName);
                if (!userId.HasValue)
                {
                    result.Add("Permission", sourceRef, "", ItemCopyStatus.Warning,
                        $"principal '{ra.Member.Title}' unresolved; assignment skipped");
                    continue;
                }
                principal = _targetCtx.Web.GetUserById(userId.Value);
            }

            var bindings = new RoleDefinitionBindingCollection(_targetCtx);
            var bound = 0;
            foreach (var rd in ra.RoleDefinitionBindings.AsEnumerable())
            {
                if (rd.Name == "Limited Access") continue;
                if (targetRoleDefs.TryGetValue(rd.Name, out var targetRd))
                {
                    bindings.Add(targetRd);
                    bound++;
                }
            }
            if (bound == 0) continue;

            targetItem.RoleAssignments.Add(principal, bindings);
            await _targetCtx.ExecuteQueryAsync();
            applied++;
        }
        result.Add("Permission", sourceRef, "", ItemCopyStatus.Copied, $"{applied} role assignment(s)");
    }

    private async Task<Group?> ResolveGroupAsync(string groupName)
    {
        // Mapping CSV may rename groups across tenants.
        if (_groupMap.TryGetValue(groupName, out var mapped)) groupName = mapped;
        if (_targetGroupsByName == null)
        {
            var groups = _targetCtx.Web.SiteGroups;
            _targetCtx.Load(groups, gs => gs.Include(g => g.Title));
            await _targetCtx.ExecuteQueryAsync();
            _targetGroupsByName = groups.AsEnumerable()
                .GroupBy(g => g.Title, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        }
        return _targetGroupsByName.GetValueOrDefault(groupName);
    }
}

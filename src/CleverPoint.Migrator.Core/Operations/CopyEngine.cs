using CleverPoint.Migrator.Core.Csom;
using CleverPoint.Migrator.Core.Model;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.Core.Operations;

/// <summary>
/// Facade for one complete list/library copy: schema, then content, with
/// user resolution shared across both. Source and target may be the same
/// web, different webs, or different tenants.
/// </summary>
public static class CopyEngine
{
    public static async Task<CopyResult> CopyListAsync(
        SpConnection source, SpConnection target, string sourceListTitle, CopyOptions options,
        Dictionary<string, string>? userMap = null, CancellationToken cancellationToken = default,
        CopyResult? liveResult = null, Dictionary<string, string>? groupMap = null)
    {
        // A caller-supplied result receives records LIVE (RecordAdded fires
        // per item), so history persists even when a run is cancelled mid-way.
        var result = liveResult ?? new CopyResult();
        Diagnostics.TraceLog.Write("Copy", $"start '{sourceListTitle}' {source.SiteUrl} -> '{options.TargetListTitle}' {target.SiteUrl}");
        using var sourceCtx = source.CreateContext();
        using var targetCtx = target.CreateContext();

        var sourceList = sourceCtx.Web.Lists.GetByTitle(sourceListTitle);
        sourceCtx.Load(sourceList, l => l.BaseType, l => l.Title, l => l.BaseTemplate);
        await sourceCtx.ExecuteQueryAsync();

        var users = new UserResolver(sourceCtx, targetCtx, userMap, options.UnresolvedUserFallback);
        await users.PrimeSourceUsersAsync();

        // Schema dependencies first (site columns, content types on the
        // target web), skipped when source and target are the same web.
        sourceCtx.Load(sourceCtx.Web, w => w.Id);
        targetCtx.Load(targetCtx.Web, w => w.Id);
        await sourceCtx.ExecuteQueryAsync();
        await targetCtx.ExecuteQueryAsync();
        if (sourceCtx.Web.Id != targetCtx.Web.Id)
        {
            var deps = new DependencyCopier(sourceCtx, targetCtx);
            await deps.CopyListDependenciesAsync(sourceList, options, result);
        }

        var schema = new SchemaCopier(sourceCtx, targetCtx);
        var targetList = await schema.CopyAsync(sourceList, options, result);

        // Lookup translation maps: identity for same-web lookups, otherwise
        // match source and target lookup items by their display value.
        var lookupMaps = new Dictionary<string, Dictionary<int, int>?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (fieldName, sourceListId, targetListId, showField) in schema.LookupFields)
        {
            if (sourceListId == targetListId)
            {
                lookupMaps[fieldName] = null;
                continue;
            }
            lookupMaps[fieldName] = await BuildLookupMapAsync(sourceCtx, targetCtx, sourceListId, targetListId, showField);
        }

        if (!options.CopyContent)
        {
            result.Add("List", sourceListTitle, options.TargetListTitle, ItemCopyStatus.Skipped, "schema-only copy: content skipped by settings");
            result.FinishedUtc = DateTime.UtcNow;
            return result;
        }

        // Modern/wiki page libraries (BaseTemplate 119) can't be copied as plain
        // files: SharePoint denies uploading .aspx via app-only, and pages store
        // their content in list fields. Route them through the Pages API copier.
        if (sourceList.BaseTemplate == 119)
        {
            var pageCopier = new PageCopier(source, target, options.ExistingMode)
            {
                IncludeNames = BuildIncludeNames(options),
            };
            await pageCopier.CopyPagesAsync(result, cancellationToken);
            result.FinishedUtc = DateTime.UtcNow;
            Diagnostics.TraceLog.Write("Copy", $"done '{sourceListTitle}' (pages): {result.Summary()}");
            return result;
        }

        if (sourceList.BaseType == BaseType.DocumentLibrary)
        {
            var copier = new FileCopier(sourceCtx, targetCtx, users, source.Rest, target.Rest, source, target)
            {
                LookupMaps = lookupMaps,
                CancellationToken = cancellationToken,
                FieldNameMap = options.FieldMap.Count > 0 ? options.FieldMap : null,
            };
            copier.SetDeltaSkipLog(result);
            await copier.CopyAsync(sourceList, targetList, options, result);
            result.FileHashes = copier.SourceHashes;
            result.MaxSourceModifiedUtc = copier.LastScanMaxModifiedUtc;
        }
        else
        {
            var copier = new ItemCopier(sourceCtx, targetCtx, users)
            {
                LookupMaps = lookupMaps,
                CancellationToken = cancellationToken,
                DeltaSkipLog = result,
                ContentTypeMap = schema.ContentTypeMap,
                FieldNameMap = options.FieldMap.Count > 0 ? options.FieldMap : null,
                TermMap = options.TermMap,
            };
            if (options.CopyPermissions)
            {
                var perms = new PermissionCopier(sourceCtx, targetCtx, users, groupMap);
                copier.Permissions = perms;
                copier.UniquePermissionItemIds = await perms.FindUniquePermissionItemsAsync(sourceList);
            }
            await copier.CopyAsync(sourceList, targetList, options, result);
            result.MaxSourceModifiedUtc = copier.LastScanMaxModifiedUtc;
        }

        foreach (var (login, reason) in users.Unresolved)
            result.Add("User", login, options.UnresolvedUserFallback ?? "(dropped)", ItemCopyStatus.Warning, $"unresolved user: {reason}");

        result.FinishedUtc = DateTime.UtcNow;
        Diagnostics.TraceLog.Write("Copy", $"done '{sourceListTitle}': {result.Summary()}");
        return result;
    }

    /// <summary>Page file names to include (from a specific selection), or null for all pages.</summary>
    private static HashSet<string>? BuildIncludeNames(CopyOptions options)
    {
        if (options.SelectedPaths is null || options.SelectedPaths.Count == 0) return null;
        var names = options.SelectedPaths
            .Select(p => p.Split('/')[^1])
            .Where(n => n.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase));
        var set = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        // A selection that contains no .aspx (e.g. only a folder was picked) means "these
        // specific pages" = none. Return the empty set, NOT null, so we do not fall back to
        // copying every page in the library.
        return set;
    }

    /// <summary>
    /// Maps source lookup item ids to target ids by matching the lookup's
    /// display column value (robust against item id drift between lists).
    /// </summary>
    private static async Task<Dictionary<int, int>> BuildLookupMapAsync(
        ClientContext sourceCtx, ClientContext targetCtx, Guid sourceListId, Guid targetListId, string showField)
    {
        async Task<List<(int Id, string Value)>> LoadAsync(ClientContext ctx, Guid listId)
        {
            var list = ctx.Web.Lists.GetById(listId);
            var rows = new List<(int, string)>();
            var query = new CamlQuery { ViewXml = "<View Scope='RecursiveAll'><RowLimit Paged='TRUE'>500</RowLimit></View>" };
            do
            {
                var page = list.GetItems(query);
                ctx.Load(page);
                ctx.Load(page, p => p.ListItemCollectionPosition);
                await ctx.ExecuteQueryAsync();
                foreach (var item in page)
                    rows.Add((item.Id, item.FieldValues.GetValueOrDefault(showField)?.ToString() ?? ""));
                query.ListItemCollectionPosition = page.ListItemCollectionPosition;
            } while (query.ListItemCollectionPosition != null);
            return rows;
        }

        var sourceRows = await LoadAsync(sourceCtx, sourceListId);
        var targetByValue = (await LoadAsync(targetCtx, targetListId))
            .GroupBy(r => r.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

        var map = new Dictionary<int, int>();
        foreach (var (id, value) in sourceRows)
            if (targetByValue.TryGetValue(value, out var targetId))
                map[id] = targetId;
        return map;
    }
}

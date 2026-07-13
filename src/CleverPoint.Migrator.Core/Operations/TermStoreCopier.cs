using CleverPoint.Migrator.Core.Model;
using Microsoft.SharePoint.Client;
using Microsoft.SharePoint.Client.Taxonomy;

namespace CleverPoint.Migrator.Core.Operations;

/// <summary>
/// Replicates the managed metadata a list depends on into the TARGET term store, and produces the
/// maps the schema/item copiers need to bind onto it.
///
/// What has to happen depends on BOTH the store and the term set's group scope -- "same tenant" alone
/// is not enough to conclude there is nothing to do:
///
///  - Same SITE COLLECTION: every term set is reachable and every GUID valid. Nothing to do.
///  - Same TERM STORE, different site collection:
///      * a TENANT term set is visible tenant-wide -> already usable, left alone;
///      * a SITE-COLLECTION term set belongs to the SOURCE site and cannot be used from the target
///        site, so it is re-homed under the target site's own group. Its GUIDs are still taken (same
///        store!), so the new terms get fresh ones and item values are remapped via <see cref="TermMap"/>.
///  - Different TERM STORE (cross-tenant): nothing exists there. Everything is replicated. Binding a
///    target column to the source SspId would produce a dead column, and writing a source term GUID
///    fails with "The given guid does not exist in the term store".
///
/// Term GUIDs are PRESERVED wherever the target store allows it (CreateTermSet/CreateTerm both accept
/// an explicit id -- this is how Import-PnPTermGroup round-trips a group). Preserving keeps the maps as
/// identity and makes re-runs idempotent. Where a GUID is taken, or a term of the same name already
/// exists, the existing target term is reused and a real remap is recorded instead.
///
/// The term store is eventually consistent and the taxonomy session caches reads, so a group or term set
/// created moments ago can still be invisible; creates that lose that race fall back to reusing the
/// winner's object rather than failing or duplicating.
/// </summary>
public class TermStoreCopier
{
    private readonly ClientContext _sourceCtx;
    private readonly ClientContext _targetCtx;

    /// <summary>The TARGET term store id. This is what taxonomy columns must bind their SspId to.</summary>
    public Guid TargetSspId { get; private set; }

    /// <summary>True when source and target resolve to the same term store (same tenant).</summary>
    public bool SameStore { get; private set; }

    /// <summary>True when source and target are the same site collection (nothing to replicate at all).</summary>
    public bool SameSite { get; private set; }

    /// <summary>Source term set id -> target term set id. Lookups are identity for anything absent.</summary>
    public Dictionary<Guid, Guid> TermSetMap { get; } = new();

    /// <summary>Source term id -> target term id. Entries only differ where a GUID could not be preserved.</summary>
    public Dictionary<Guid, Guid> TermMap { get; } = new();

    /// <summary>True when the target store was reachable and any needed term sets are in place.</summary>
    public bool Ready { get; private set; }

    public TermStoreCopier(ClientContext sourceCtx, ClientContext targetCtx)
    {
        _sourceCtx = sourceCtx;
        _targetCtx = targetCtx;
    }

    /// <summary>
    /// Ensures every term set referenced by the source list's taxonomy columns exists in the target
    /// term store, and fills <see cref="TargetSspId"/>, <see cref="TermSetMap"/> and <see cref="TermMap"/>.
    /// A no-op (Ready=false) when the list has no taxonomy columns.
    /// </summary>
    public async Task PrepareAsync(List sourceList, CopyResult result)
    {
        var taxFields = sourceList.Fields.AsEnumerable()
            .Where(f => f.TypeAsString is "TaxonomyFieldType" or "TaxonomyFieldTypeMulti")
            .ToList();
        if (taxFields.Count == 0) return;

        var sourceSession = TaxonomySession.GetTaxonomySession(_sourceCtx);
        var sourceStore = sourceSession.GetDefaultSiteCollectionTermStore();
        _sourceCtx.Load(sourceStore, s => s.Id);
        _sourceCtx.Load(_sourceCtx.Site, s => s.Id);

        var targetSession = TaxonomySession.GetTaxonomySession(_targetCtx);
        var targetStore = targetSession.GetDefaultSiteCollectionTermStore();
        _targetCtx.Load(targetStore, s => s.Id, s => s.DefaultLanguage);
        _targetCtx.Load(_targetCtx.Site, s => s.Id);

        try
        {
            await _sourceCtx.ExecuteQueryAsync();
            await _targetCtx.ExecuteQueryAsync();
        }
        catch (Exception ex)
        {
            result.Add("TermStore", "term store", "", ItemCopyStatus.Warning,
                $"could not open a term store on both sides: {ex.Message}");
            return;
        }

        TargetSspId = targetStore.Id;
        SameStore = sourceStore.Id == targetStore.Id;
        SameSite = SameStore && _sourceCtx.Site.Id == _targetCtx.Site.Id;
        Ready = true;

        if (SameSite)
        {
            // Same site collection: every term set the list uses -- tenant or site-collection scoped --
            // is already reachable, and every GUID is already valid. Nothing to do.
            Diagnostics.TraceLog.Write("Taxonomy", $"same site collection, term store {TargetSspId:D}: bindings carry over unchanged");
            return;
        }

        Diagnostics.TraceLog.Write("Taxonomy",
            SameStore
                ? $"same term store {TargetSspId:D}, different site collection: tenant term sets carry over; site-collection term sets must be re-homed"
                : $"cross-store copy: source {sourceStore.Id:D} -> target {targetStore.Id:D}; replicating {taxFields.Count} column(s)");

        // Distinct term sets: several columns can point at the same one.
        var termSetIds = new HashSet<Guid>();
        foreach (var field in taxFields)
        {
            var tax = _sourceCtx.CastTo<TaxonomyField>(field);
            _sourceCtx.Load(tax, f => f.TermSetId, f => f.InternalName);
            await _sourceCtx.ExecuteQueryAsync();
            if (tax.TermSetId != Guid.Empty) termSetIds.Add(tax.TermSetId);
        }

        foreach (var termSetId in termSetIds)
        {
            try
            {
                await ReplicateTermSetAsync(sourceStore, targetStore, termSetId, result);
            }
            catch (Exception ex)
            {
                result.Add("TermSet", termSetId.ToString(), "", ItemCopyStatus.Failed,
                    $"term set could not be replicated to the target term store: {ex.Message}");
                Diagnostics.TraceLog.Write("Taxonomy", $"term set {termSetId:D} FAILED: {ex.Message}");
            }
        }
    }

    /// <summary>Recreates one source term set (and its terms) under the right group in the target store.</summary>
    private async Task ReplicateTermSetAsync(TermStore sourceStore, TermStore targetStore, Guid sourceTermSetId, CopyResult result)
    {
        var sourceSet = sourceStore.GetTermSet(sourceTermSetId);
        _sourceCtx.Load(sourceSet, ts => ts.Id, ts => ts.Name, ts => ts.IsAvailableForTagging, ts => ts.IsOpenForTermCreation,
            ts => ts.Group.Id, ts => ts.Group.Name, ts => ts.Group.IsSiteCollectionGroup);
        var sourceTerms = sourceSet.GetAllTerms();
        _sourceCtx.Load(sourceTerms, ts => ts.Include(t => t.Id, t => t.Name, t => t.PathOfTerm, t => t.IsAvailableForTagging));
        await _sourceCtx.ExecuteQueryAsync();

        var siteScoped = sourceSet.Group.IsSiteCollectionGroup;
        var scope = siteScoped ? "site-collection" : "tenant";
        Diagnostics.TraceLog.Write("Taxonomy",
            $"term set '{sourceSet.Name}' ({sourceTermSetId:D}) in {scope} group '{sourceSet.Group.Name}', {sourceTerms.Count} term(s)");

        // A TENANT term set in the SAME store is visible from every site in the tenant: it is already
        // usable at the target, GUIDs and all. Nothing to replicate.
        //
        // A SITE-COLLECTION term set is not. It belongs to the SOURCE site collection's group, and no
        // other site collection can use it -- even inside the same tenant, sharing the same term store.
        // It has to be re-homed under the TARGET site's own group, which means new term GUIDs (the old
        // ones are still taken by the source's copy in that same store) and therefore a real term map.
        if (SameStore && !siteScoped)
        {
            TermSetMap[sourceTermSetId] = sourceTermSetId;
            result.Add("TermSet", sourceSet.Name, sourceSet.Name, ItemCopyStatus.Skipped,
                "tenant term set, same term store: already available on target");
            return;
        }

        // 1. The target GROUP. A site-collection group belongs to its site: the target equivalent is
        //    the TARGET site's own site-collection group, never the source group's GUID.
        TermGroup targetGroup;
        if (siteScoped)
        {
            targetGroup = targetStore.GetSiteCollectionGroup(_targetCtx.Site, true);
            _targetCtx.Load(targetGroup, g => g.Id, g => g.Name);
            await _targetCtx.ExecuteQueryAsync();
        }
        else
        {
            targetGroup = await EnsureTenantGroupAsync(targetStore, sourceSet.Group.Name, result);
        }

        // 2. The target TERM SET. Prefer the source GUID -- but only if that term set actually sits in
        //    the group we resolved above. Same-store re-homing is exactly the case where GetTermSet(id)
        //    DOES resolve, to the source site collection's copy: binding to it would leave the target
        //    site pointing at a term set it cannot use. Fall back to name-within-group, then create.
        var targetSet = await FindTermSetByIdInGroupAsync(targetStore, sourceTermSetId, targetGroup.Id)
                        ?? await FindTermSetByNameAsync(targetGroup, sourceSet.Name);

        if (targetSet == null)
        {
            targetSet = await CreateTermSetAsync(targetStore, targetGroup, sourceSet, sourceTermSetId);
            result.Add("TermSet", sourceSet.Name, sourceSet.Name, ItemCopyStatus.Copied,
                $"created in {scope} group '{targetGroup.Name}'");
        }
        else
        {
            result.Add("TermSet", sourceSet.Name, targetSet.Name, ItemCopyStatus.Skipped,
                $"already in target term store ({scope} group '{targetGroup.Name}')");
        }

        TermSetMap[sourceTermSetId] = targetSet.Id;
        if (targetSet.Id != sourceTermSetId)
            Diagnostics.TraceLog.Write("Taxonomy", $"term set remapped {sourceTermSetId:D} -> {targetSet.Id:D}");

        // 3. The TERMS.
        await ReplicateTermsAsync(targetStore, targetSet, sourceTerms, result);
    }

    /// <summary>
    /// Finds (or creates) a normal tenant group by name in the target store.
    ///
    /// The term store is eventually consistent and the taxonomy session caches reads, so a group created
    /// moments ago -- by the previous list in the same batch, say -- can still be invisible here. Creating
    /// it again then fails with "Group names must be unique". Treat a failed create as "someone beat me
    /// to it": refresh the cache and reuse theirs.
    /// </summary>
    private async Task<TermGroup> EnsureTenantGroupAsync(TermStore targetStore, string name, CopyResult result)
    {
        var existing = await FindTenantGroupAsync(targetStore, name, refreshCache: false);
        if (existing != null) return existing;

        try
        {
            var created = targetStore.CreateGroup(name, Guid.NewGuid());
            targetStore.CommitAll();
            _targetCtx.Load(created, g => g.Id, g => g.Name);
            await _targetCtx.ExecuteQueryAsync();
            result.Add("TermGroup", name, name, ItemCopyStatus.Copied, "created in target term store");
            Diagnostics.TraceLog.Write("Taxonomy", $"created tenant term group '{name}' ({created.Id:D})");
            return created;
        }
        catch (Exception ex)
        {
            Diagnostics.TraceLog.Write("Taxonomy",
                $"term group '{name}' create failed ({ex.Message}); refreshing the term store cache and re-reading");
            var raced = await FindTenantGroupAsync(targetStore, name, refreshCache: true)
                ?? throw new InvalidOperationException($"term group '{name}' could not be created or found: {ex.Message}", ex);
            result.Add("TermGroup", name, name, ItemCopyStatus.Skipped, "already in target term store");
            return raced;
        }
    }

    /// <summary>
    /// Creates the term set, preferring the SOURCE GUID so the item copy needs no remap. Two things can
    /// refuse it: the GUID is already taken in this store (same-store re-homing -- the source's own copy
    /// still holds it), or the name already exists in the group because a concurrent/earlier copy in this
    /// batch created it and our cached read missed it. Check for the latter before claiming a fresh GUID,
    /// otherwise we would create a duplicate term set.
    /// </summary>
    private async Task<TermSet> CreateTermSetAsync(TermStore targetStore, TermGroup targetGroup, TermSet sourceSet, Guid sourceTermSetId)
    {
        try
        {
            var set = targetGroup.CreateTermSet(sourceSet.Name, sourceTermSetId, targetStore.DefaultLanguage);
            set.IsAvailableForTagging = sourceSet.IsAvailableForTagging;
            set.IsOpenForTermCreation = sourceSet.IsOpenForTermCreation;
            targetStore.CommitAll();
            _targetCtx.Load(set, ts => ts.Id, ts => ts.Name);
            await _targetCtx.ExecuteQueryAsync();
            return set;
        }
        catch (Exception ex)
        {
            Diagnostics.TraceLog.Write("Taxonomy",
                $"term set '{sourceSet.Name}' with GUID {sourceTermSetId:D} refused ({ex.Message}); refreshing and re-checking");
        }

        // Did someone else already create it under this group while our read was stale?
        TaxonomySession.GetTaxonomySession(_targetCtx).UpdateCache();
        await _targetCtx.ExecuteQueryAsync();
        var raced = await FindTermSetByNameAsync(targetGroup, sourceSet.Name);
        if (raced != null)
        {
            Diagnostics.TraceLog.Write("Taxonomy", $"term set '{sourceSet.Name}' already existed ({raced.Id:D}); reusing");
            return raced;
        }

        // No: the GUID itself was taken (re-homing inside one term store). Claim a fresh one; the terms
        // below get fresh GUIDs too, and TermMap carries the item values across.
        var fresh = targetGroup.CreateTermSet(sourceSet.Name, Guid.NewGuid(), targetStore.DefaultLanguage);
        targetStore.CommitAll();
        _targetCtx.Load(fresh, ts => ts.Id, ts => ts.Name);
        await _targetCtx.ExecuteQueryAsync();
        Diagnostics.TraceLog.Write("Taxonomy", $"term set '{sourceSet.Name}' created with a new id {fresh.Id:D}");
        return fresh;
    }

    private async Task<TermGroup?> FindTenantGroupAsync(TermStore store, string name, bool refreshCache)
    {
        if (refreshCache)
        {
            TaxonomySession.GetTaxonomySession(_targetCtx).UpdateCache();
            await _targetCtx.ExecuteQueryAsync();
        }

        var groups = store.Groups;
        _targetCtx.Load(groups, gs => gs.Include(g => g.Id, g => g.Name, g => g.IsSiteCollectionGroup));
        await _targetCtx.ExecuteQueryAsync();

        return groups.AsEnumerable()
            .FirstOrDefault(g => !g.IsSiteCollectionGroup && string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The term set with this id, but ONLY if it lives in the expected group. Same-store re-homing of a
    /// site-collection term set is the case that matters: the id resolves to the SOURCE site's copy,
    /// which the target site cannot use, so it must be rejected rather than reused.
    /// </summary>
    private async Task<TermSet?> FindTermSetByIdInGroupAsync(TermStore store, Guid id, Guid expectedGroupId)
    {
        var set = store.GetTermSet(id);
        _targetCtx.Load(set, ts => ts.Id, ts => ts.Name, ts => ts.Group.Id);
        await _targetCtx.ExecuteQueryAsync();
        if (set.ServerObjectIsNull == true) return null;
        return set.Group.Id == expectedGroupId ? set : null;
    }

    private async Task<TermSet?> FindTermSetByNameAsync(TermGroup group, string name)
    {
        _targetCtx.Load(group.TermSets, ts => ts.Include(t => t.Id, t => t.Name));
        await _targetCtx.ExecuteQueryAsync();
        return group.TermSets.AsEnumerable()
            .FirstOrDefault(ts => string.Equals(ts.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Recreates the source terms under the target term set, parents before children. Terms already
    /// present (by GUID, or by name at the same path) are reused; the rest are created with the source
    /// GUID so the item copy needs no remap at all.
    /// </summary>
    private async Task ReplicateTermsAsync(TermStore targetStore, TermSet targetSet, TermCollection sourceTerms, CopyResult result)
    {
        // Existing target terms, keyed by their path ("Parent;Child").
        var existingTerms = targetSet.GetAllTerms();
        _targetCtx.Load(existingTerms, ts => ts.Include(t => t.Id, t => t.Name, t => t.PathOfTerm));
        await _targetCtx.ExecuteQueryAsync();

        var targetByPath = existingTerms.AsEnumerable()
            .GroupBy(t => t.PathOfTerm, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // Parents must exist before children: order by depth.
        var ordered = sourceTerms.AsEnumerable()
            .OrderBy(t => t.PathOfTerm.Count(c => c == ';'))
            .ThenBy(t => t.PathOfTerm, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Counted per term set: TermMap accumulates across every term set the list uses.
        var mapped = 0;
        var reGuided = 0;

        foreach (var sourceTerm in ordered)
        {
            var path = sourceTerm.PathOfTerm;

            if (targetByPath.TryGetValue(path, out var already))
            {
                TermMap[sourceTerm.Id] = already.Id;
                mapped++;
                if (already.Id != sourceTerm.Id) reGuided++;
                continue;
            }

            // Parent: the term set itself for a root term, else the already-created parent term.
            var sep = path.LastIndexOf(';');
            TermSetItem parent = targetSet;
            if (sep >= 0)
            {
                var parentPath = path[..sep];
                if (!targetByPath.TryGetValue(parentPath, out var parentTerm))
                {
                    result.Add("Term", path, "", ItemCopyStatus.Warning, "parent term missing on target; term skipped");
                    continue;
                }
                parent = parentTerm;
            }

            var created = await CreateTermAsync(targetStore, parent, sourceTerm, path);
            if (created == null)
            {
                result.Add("Term", path, "", ItemCopyStatus.Failed, "term could not be created in the target term store");
                continue;
            }
            targetByPath[path] = created;
            TermMap[sourceTerm.Id] = created.Id;
            mapped++;
            if (created.Id != sourceTerm.Id)
            {
                reGuided++;
                Diagnostics.TraceLog.Write("Taxonomy", $"term '{path}' remapped {sourceTerm.Id:D} -> {created.Id:D}");
            }
        }

        Diagnostics.TraceLog.Write("Taxonomy",
            $"term set '{targetSet.Name}': {mapped} term(s) mapped, {reGuided} needed a new GUID");
        result.Add("TermSet", targetSet.Name, targetSet.Name, ItemCopyStatus.Copied,
            $"{mapped} term(s) available on target ({reGuided} remapped)");
    }

    /// <summary>Creates one term, preferring the source GUID and falling back to a fresh one if it is refused.</summary>
    private async Task<Term?> CreateTermAsync(TermStore targetStore, TermSetItem parent, Term sourceTerm, string path)
    {
        try
        {
            var term = parent.CreateTerm(sourceTerm.Name, targetStore.DefaultLanguage, sourceTerm.Id);
            term.IsAvailableForTagging = sourceTerm.IsAvailableForTagging;
            targetStore.CommitAll();
            _targetCtx.Load(term, t => t.Id, t => t.Name, t => t.PathOfTerm);
            await _targetCtx.ExecuteQueryAsync();
            return term;
        }
        catch (Exception ex)
        {
            Diagnostics.TraceLog.Write("Taxonomy", $"term '{path}' GUID {sourceTerm.Id:D} refused ({ex.Message}); retrying with a new id");
        }

        // The failed ExecuteQuery left the parent handle stale: re-fetch it before retrying.
        try
        {
            var freshParent = await RefetchParentAsync(targetStore, parent);
            if (freshParent == null) return null;
            var term = freshParent.CreateTerm(sourceTerm.Name, targetStore.DefaultLanguage, Guid.NewGuid());
            term.IsAvailableForTagging = sourceTerm.IsAvailableForTagging;
            targetStore.CommitAll();
            _targetCtx.Load(term, t => t.Id, t => t.Name, t => t.PathOfTerm);
            await _targetCtx.ExecuteQueryAsync();
            return term;
        }
        catch (Exception ex)
        {
            Diagnostics.TraceLog.Write("Taxonomy", $"term '{path}' could not be created: {ex.Message}");
            return null;
        }
    }

    /// <summary>Re-reads a term set / term handle from the store after a failed batch invalidated it.</summary>
    private async Task<TermSetItem?> RefetchParentAsync(TermStore targetStore, TermSetItem parent)
    {
        _targetCtx.Load(parent, p => p.Id);
        await _targetCtx.ExecuteQueryAsync();

        TermSetItem? fresh = parent is TermSet
            ? targetStore.GetTermSet(parent.Id)
            : targetStore.GetTerm(parent.Id);
        _targetCtx.Load(fresh, p => p.Id);
        await _targetCtx.ExecuteQueryAsync();
        return fresh.ServerObjectIsNull == true ? null : fresh;
    }

    /// <summary>Maps a source term set id onto the target store (identity when same-tenant / GUID preserved).</summary>
    public Guid MapTermSet(Guid sourceTermSetId) =>
        TermSetMap.TryGetValue(sourceTermSetId, out var t) ? t : sourceTermSetId;

    /// <summary>Maps a source term id onto the target store (identity when same-tenant / GUID preserved).</summary>
    public Guid MapTerm(Guid sourceTermId) =>
        TermMap.TryGetValue(sourceTermId, out var t) ? t : sourceTermId;

    /// <summary>
    /// The term GUID remap to hand the item copiers: null when nothing actually moved (same tenant,
    /// or every GUID was preserved), so the copiers keep their identity fast path.
    /// </summary>
    public Dictionary<Guid, Guid>? ItemTermMap()
    {
        var moved = TermMap.Where(kv => kv.Key != kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value);
        return moved.Count > 0 ? moved : null;
    }
}

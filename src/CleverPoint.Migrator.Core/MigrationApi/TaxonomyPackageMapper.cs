using Microsoft.SharePoint.Client;
using Microsoft.SharePoint.Client.Taxonomy;

namespace CleverPoint.Migrator.Core.MigrationApi;

/// <summary>
/// Teaches the Migration API package how to carry managed metadata.
///
/// The import writes straight to the content DB and does NOT fire event receivers -- and taxonomy on a
/// normal item write is entirely the work of two receivers (TaxonomyItemSynchronousAdded /
/// TaxonomyItemUpdating). So the package has to write, itself, exactly what those receivers would have:
///
///   1. the taxonomy column, which is a LOOKUP into the site's hidden TaxonomyHiddenList:
///          Value = "WssId;#Label|TermGuid"        (multi: joined with ";#")
///   2. its companion hidden Note field:
///          Value = "Label|TermGuid"               (multi: joined with ";")
///
/// The WssId is an ITEM ID in TaxonomyHiddenList, per site collection. A term that has never been used
/// in the target site has no row there and therefore no WssId -- and the import cannot create one. So
/// before packaging, this seeds them: it tags one throwaway item through normal CSOM (which DOES fire
/// the receivers, creating the rows), reads the WssIds straight off the tagged values, and deletes it.
/// The TaxonomyHiddenList rows survive the delete, which is what makes this work.
///
/// The note field's internal name is NOT the field GUID with dashes stripped: SharePoint cannot start an
/// internal name with a digit, so a leading digit is shifted into a letter ('2' -> 'i', '4' -> 'k'). It is
/// read from TaxonomyField.TextField instead of reproducing that rule.
/// </summary>
public class TaxonomyPackageMapper
{
    private readonly ClientContext _targetCtx;
    private readonly List _targetList;

    /// <summary>One taxonomy column on the target list.</summary>
    private class TaxColumn
    {
        public string InternalName = "";
        public string NoteInternalName = "";
        public bool Multi;
        public Guid SspId;
        public Guid TermSetId;
        public TaxonomyField Field = null!;
    }

    private readonly Dictionary<string, TaxColumn> _columns = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Target term GUID -> WssId (its row in the target site's TaxonomyHiddenList).</summary>
    private readonly Dictionary<Guid, int> _wssIds = new();

    /// <summary>Terms that could not be given a WssId; their values are dropped and reported.</summary>
    public List<string> UnresolvedTerms { get; } = new();

    /// <summary>True once PrimeAsync has run and there is at least one taxonomy column to carry.</summary>
    public bool HasTaxonomy => _columns.Count > 0;

    public TaxonomyPackageMapper(ClientContext targetCtx, List targetList)
    {
        _targetCtx = targetCtx;
        _targetList = targetList;
    }

    /// <summary>
    /// Reads the target list's taxonomy columns and seeds a WssId for every term the source items use.
    /// <paramref name="sourceItems"/> supply the terms; <paramref name="mapTerm"/> maps a source term GUID
    /// to its target-store equivalent (identity when the GUIDs were preserved).
    /// </summary>
    public async Task PrimeAsync(
        List<(string InternalName, string TypeAsString)> copyFields,
        IEnumerable<ListItem> sourceItems,
        Func<string, string> mapTerm,
        Dictionary<string, string>? fieldNameMap,
        Action<string>? log = null)
    {
        var taxFields = copyFields
            .Where(f => f.TypeAsString is "TaxonomyFieldType" or "TaxonomyFieldTypeMulti")
            .ToList();
        if (taxFields.Count == 0) return;

        // ---- The target columns, plus their hidden note fields ----
        var pending = new List<(string Source, TaxonomyField Tax)>();
        foreach (var (name, _) in taxFields)
        {
            var write = fieldNameMap?.GetValueOrDefault(name) ?? name;
            var tf = _targetCtx.CastTo<TaxonomyField>(_targetList.Fields.GetByInternalNameOrTitle(write));
            _targetCtx.Load(tf, f => f.Id, f => f.InternalName, f => f.TextField, f => f.SspId,
                f => f.TermSetId, f => f.AllowMultipleValues);
            pending.Add((name, tf));
        }
        await _targetCtx.ExecuteQueryAsync();

        foreach (var (sourceName, tf) in pending)
        {
            var note = _targetList.Fields.GetById(tf.TextField);
            _targetCtx.Load(note, f => f.InternalName);
            await _targetCtx.ExecuteQueryAsync();

            _columns[sourceName] = new TaxColumn
            {
                InternalName = tf.InternalName,
                NoteInternalName = note.InternalName,
                Multi = tf.AllowMultipleValues,
                SspId = tf.SspId,
                TermSetId = tf.TermSetId,
                Field = tf,
            };
        }

        // ---- Every distinct TARGET term the copy will write, grouped by term set ----
        var termsBySet = new Dictionary<Guid, Dictionary<Guid, string>>();   // termSet -> (termId -> label)
        foreach (var item in sourceItems)
        {
            foreach (var (sourceName, col) in _columns)
            {
                foreach (var (label, guid) in ReadSourceTerms(item.FieldValues.GetValueOrDefault(sourceName)))
                {
                    if (!Guid.TryParse(mapTerm(guid), out var targetTerm)) continue;
                    if (!termsBySet.TryGetValue(col.TermSetId, out var set))
                        termsBySet[col.TermSetId] = set = new Dictionary<Guid, string>();
                    set[targetTerm] = label;
                }
            }
        }
        if (termsBySet.Count == 0) return;

        var distinct = termsBySet.Sum(kv => kv.Value.Count);
        log?.Invoke($"managed metadata: seeding {distinct} distinct term(s) into the target site's taxonomy index…");
        await SeedWssIdsAsync(termsBySet, log);
    }

    /// <summary>
    /// Creates one throwaway item, tags it with every term (normal CSOM, so the taxonomy receivers run and
    /// populate TaxonomyHiddenList), reads the assigned WssIds back off the tagged values, and deletes it.
    /// </summary>
    private async Task SeedWssIdsAsync(Dictionary<Guid, Dictionary<Guid, string>> termsBySet, Action<string>? log)
    {
        // A library needs a file to hang a list item off; a list takes a plain item.
        _targetCtx.Load(_targetList, l => l.BaseType, l => l.RootFolder.ServerRelativeUrl);
        await _targetCtx.ExecuteQueryAsync();

        ListItem seed;
        Microsoft.SharePoint.Client.File? seedFile = null;
        if (_targetList.BaseType == BaseType.DocumentLibrary)
        {
            var name = $"_taxonomy-seed-{Guid.NewGuid():N}.txt";
            seedFile = _targetList.RootFolder.Files.Add(new FileCreationInformation
            {
                Url = name,
                Content = System.Text.Encoding.UTF8.GetBytes("taxonomy seed"),
                Overwrite = true,
            });
            _targetCtx.Load(seedFile, f => f.ListItemAllFields);
            await _targetCtx.ExecuteQueryAsync();
            seed = seedFile.ListItemAllFields;
        }
        else
        {
            seed = _targetList.AddItem(new ListItemCreationInformation());
            seed["Title"] = "_taxonomy-seed";
            seed.Update();
            await _targetCtx.ExecuteQueryAsync();
        }

        try
        {
            foreach (var (termSetId, terms) in termsBySet)
            {
                // Prefer a MULTI column for this term set: it seeds every term in one write. With only a
                // single-value column we have to write one term at a time.
                var multi = _columns.Values.FirstOrDefault(c => c.TermSetId == termSetId && c.Multi);
                var single = _columns.Values.FirstOrDefault(c => c.TermSetId == termSetId && !c.Multi);

                if (multi != null)
                {
                    var pairs = string.Join(";", terms.Select(t => $"{t.Value}|{t.Key}"));
                    var coll = new TaxonomyFieldValueCollection(_targetCtx, null, multi.Field);
                    coll.PopulateFromLabelGuidPairs(pairs);
                    multi.Field.SetFieldValueByValueCollection(seed, coll);
                    seed.Update();
                    await _targetCtx.ExecuteQueryAsync();
                    await HarvestAsync(seed, multi);
                }
                else if (single != null)
                {
                    foreach (var (termId, label) in terms)
                    {
                        seed[single.InternalName] = new TaxonomyFieldValue
                        {
                            WssId = -1, Label = label, TermGuid = termId.ToString(),
                        };
                        seed.Update();
                        await _targetCtx.ExecuteQueryAsync();
                        await HarvestAsync(seed, single);
                    }
                }
            }
        }
        finally
        {
            try
            {
                seed.DeleteObject();
                await _targetCtx.ExecuteQueryAsync();
                if (seedFile != null)
                {
                    // The file lands in the recycle bin; leaving it there is noise, so clear it.
                    var recycled = _targetCtx.Web.RecycleBin;
                    _targetCtx.Load(recycled, r => r.Include(e => e.Id, e => e.LeafName));
                    await _targetCtx.ExecuteQueryAsync();
                    foreach (var e in recycled.AsEnumerable()
                                 .Where(e => e.LeafName.StartsWith("_taxonomy-seed-", StringComparison.OrdinalIgnoreCase))
                                 .ToList())
                        e.DeleteObject();
                    await _targetCtx.ExecuteQueryAsync();
                }
            }
            catch (Exception ex)
            {
                Diagnostics.TraceLog.Write("Taxonomy", $"could not clean up the taxonomy seed item: {ex.Message}");
            }
        }

        var missing = termsBySet.SelectMany(kv => kv.Value.Keys).Where(t => !_wssIds.ContainsKey(t)).ToList();
        foreach (var t in missing) UnresolvedTerms.Add(t.ToString());
        log?.Invoke($"managed metadata: {_wssIds.Count} term(s) indexed"
                    + (missing.Count > 0 ? $", {missing.Count} could NOT be resolved" : ""));
    }

    /// <summary>Reads the WssIds SharePoint just assigned, straight off the seed item's tagged values.</summary>
    private async Task HarvestAsync(ListItem seed, TaxColumn column)
    {
        _targetCtx.Load(seed);
        await _targetCtx.ExecuteQueryAsync();

        var value = seed.FieldValues.GetValueOrDefault(column.InternalName);
        foreach (var v in Flatten(value))
            if (v.WssId > 0 && Guid.TryParse(v.TermGuid, out var g))
                _wssIds[g] = v.WssId;
    }

    /// <summary>The manifest &lt;Field&gt; rows for one item's taxonomy columns (empty when it has none set).</summary>
    public IEnumerable<PackageFieldValue> Emit(ListItem sourceItem, Func<string, string> mapTerm)
    {
        foreach (var (sourceName, col) in _columns)
        {
            var terms = ReadSourceTerms(sourceItem.FieldValues.GetValueOrDefault(sourceName))
                .Select(t => (t.Label, Guid: mapTerm(t.Guid)))
                .Where(t => Guid.TryParse(t.Guid, out var g) && _wssIds.ContainsKey(g))
                .ToList();
            if (terms.Count == 0) continue;

            if (!col.Multi) terms = terms.Take(1).ToList();

            // Lookup form: "WssId;#Label|TermGuid", repeated with ";#" for multi.
            var lookup = string.Join(";#", terms.Select(t => $"{_wssIds[Guid.Parse(t.Guid)]};#{t.Label}|{t.Guid}"));
            // Note form: "Label|TermGuid", repeated with ";" for multi.
            var note = string.Join(";", terms.Select(t => $"{t.Label}|{t.Guid}"));

            yield return new PackageFieldValue
            {
                Name = col.InternalName,
                Value = lookup,
                Type = col.Multi ? "TaxonomyFieldTypeMulti" : "TaxonomyFieldType",
            };
            yield return new PackageFieldValue
            {
                Name = col.NoteInternalName,
                Value = note,
                Type = "Note",
            };
        }
    }

    /// <summary>(Label, TermGuid) pairs from whatever CSOM handed back for a source taxonomy field.</summary>
    private static IEnumerable<(string Label, string Guid)> ReadSourceTerms(object? value) =>
        Flatten(value).Where(v => !string.IsNullOrEmpty(v.TermGuid)).Select(v => (v.Label, v.TermGuid));

    private static IEnumerable<TaxonomyFieldValue> Flatten(object? value) => value switch
    {
        TaxonomyFieldValueCollection col => col,
        TaxonomyFieldValue single => new[] { single },
        _ => Enumerable.Empty<TaxonomyFieldValue>(),
    };
}

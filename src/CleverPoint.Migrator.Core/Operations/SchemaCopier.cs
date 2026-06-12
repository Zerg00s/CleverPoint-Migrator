using System.Text.RegularExpressions;
using CleverPoint.Migrator.Core.Model;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.Core.Operations;

/// <summary>
/// Creates/updates the target list so its schema matches the source list:
/// base template, custom fields (via SchemaXml), core settings, and views.
/// Lookup and taxonomy fields are out of scope and recorded as warnings.
/// </summary>
public partial class SchemaCopier
{
    private static readonly HashSet<string> SkippedFieldTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "TaxonomyFieldType", "TaxonomyFieldTypeMulti",
    };

    [GeneratedRegex(@"\s(SourceID|Version|StaticName|ColName|RowOrdinal)=""[^""]*""")]
    private static partial Regex StripAttrsRegex();

    [GeneratedRegex(@"List=""\{?([0-9a-fA-F\-]{36})\}?""")]
    private static partial Regex LookupListAttrRegex();

    [GeneratedRegex(@"\sWebId=""[^""]*""")]
    private static partial Regex WebIdAttrRegex();

    private readonly ClientContext _sourceCtx;
    private readonly ClientContext _targetCtx;

    /// <summary>Lookup fields successfully wired up on the target: (internal name, source list id, target list id, show field).</summary>
    public List<(string InternalName, Guid SourceListId, Guid TargetListId, string ShowField)> LookupFields { get; } = new();

    public SchemaCopier(ClientContext sourceCtx, ClientContext targetCtx)
    {
        _sourceCtx = sourceCtx;
        _targetCtx = targetCtx;
    }

    /// <summary>
    /// Ensures the target list exists with the source's template and schema.
    /// Returns the target list, ready for item copying.
    /// </summary>
    public async Task<List> CopyAsync(List sourceList, CopyOptions options, CopyResult result)
    {
        _sourceCtx.Load(sourceList,
            l => l.Title, l => l.Description, l => l.BaseTemplate, l => l.BaseType,
            l => l.EnableVersioning, l => l.EnableMinorVersions, l => l.EnableFolderCreation,
            l => l.EnableAttachments, l => l.ContentTypesEnabled, l => l.OnQuickLaunch,
            l => l.RootFolder.ServerRelativeUrl,
            l => l.Fields.Include(f => f.InternalName, f => f.Hidden, f => f.ReadOnlyField,
                f => f.SchemaXml, f => f.TypeAsString, f => f.FromBaseType, f => f.Title, f => f.Sealed,
                f => f.CustomFormatter),
            l => l.Views.Include(v => v.Title, v => v.Hidden, v => v.PersonalView, v => v.DefaultView,
                v => v.RowLimit, v => v.Paged, v => v.ViewQuery, v => v.ViewFields, v => v.CustomFormatter));
        await _sourceCtx.ExecuteQueryAsync();

        // Find or create the target list.
        var targetWeb = _targetCtx.Web;
        var targetLists = targetWeb.Lists;
        _targetCtx.Load(targetLists, ls => ls.Include(l => l.Title, l => l.Id));
        await _targetCtx.ExecuteQueryAsync();

        var existing = targetLists.AsEnumerable().FirstOrDefault(l =>
            l.Title.Equals(options.TargetListTitle, StringComparison.OrdinalIgnoreCase));

        // Content-only mode: the target schema is sacred. Resolve the list,
        // build READ-ONLY lookup maps from fields that exist on both sides,
        // and write nothing.
        if (!options.MergeSchema)
        {
            if (existing == null)
                throw new InvalidOperationException(
                    $"Content-only copy: target list '{options.TargetListTitle}' does not exist. " +
                    "Create it first or run a structure + content copy.");
            _targetCtx.Load(existing, l => l.RootFolder.ServerRelativeUrl,
                l => l.Fields.Include(f => f.InternalName, f => f.TypeAsString, f => f.SchemaXml));
            await _targetCtx.ExecuteQueryAsync();
            foreach (var sourceField in sourceList.Fields)
            {
                if (sourceField.TypeAsString is not ("Lookup" or "LookupMulti")) continue;
                var match = existing.Fields.AsEnumerable().FirstOrDefault(f =>
                    f.InternalName.Equals(sourceField.InternalName, StringComparison.OrdinalIgnoreCase));
                if (match == null) continue;
                var srcId = ParseListId(sourceField.SchemaXml);
                var tgtId = ParseListId(match.SchemaXml);
                if (srcId == null || tgtId == null) continue;
                var show = Regex.Match(match.SchemaXml, @"ShowField=""([^""]+)""") is { Success: true } sf
                    ? sf.Groups[1].Value : "Title";
                LookupFields.Add((sourceField.InternalName, srcId.Value, tgtId.Value, show));
            }
            result.Add("List", sourceList.Title, options.TargetListTitle, ItemCopyStatus.Skipped,
                "content-only: target schema left untouched");
            return existing;
        }

        List targetList;
        if (existing != null)  // schema merge path
        {
            targetList = existing;
            result.Add("List", sourceList.Title, options.TargetListTitle, ItemCopyStatus.Skipped, "target list already exists; schema merged");
        }
        else
        {
            var creation = new ListCreationInformation
            {
                Title = options.TargetListTitle,
                TemplateType = sourceList.BaseTemplate,
                Description = sourceList.Description,
            };
            if (!string.IsNullOrEmpty(options.TargetListUrl))
                creation.Url = options.TargetListUrl;
            targetList = targetWeb.Lists.Add(creation);
            await _targetCtx.ExecuteQueryAsync();
            result.Add("List", sourceList.Title, options.TargetListTitle, ItemCopyStatus.Copied, $"created (template {sourceList.BaseTemplate})");
        }

        // Load target fields for the merge.
        _targetCtx.Load(targetList, l => l.Fields.Include(f => f.InternalName), l => l.RootFolder.ServerRelativeUrl);
        await _targetCtx.ExecuteQueryAsync();
        var targetFieldNames = new HashSet<string>(
            targetList.Fields.AsEnumerable().Select(f => f.InternalName), StringComparer.OrdinalIgnoreCase);

        // Copy custom fields.
        foreach (var field in sourceList.Fields)
        {
            if (field.FromBaseType || field.Hidden || field.Sealed) continue;
            if (field.ReadOnlyField && field.InternalName != "Created" && field.InternalName != "Modified") continue;
            if (field.InternalName is "Title" or "ContentType" or "Attachments") continue;
            if (targetFieldNames.Contains(field.InternalName))
            {
                // Field already there (merge into existing list): still sync
                // the column formatting JSON, which evolves independently.
                if (!string.IsNullOrEmpty(field.CustomFormatter))
                {
                    try
                    {
                        var existingField = targetList.Fields.GetByInternalNameOrTitle(field.InternalName);
                        existingField.CustomFormatter = field.CustomFormatter;
                        existingField.UpdateAndPushChanges(true);
                        await _targetCtx.ExecuteQueryAsync();
                        result.Add("Field", field.InternalName, field.InternalName, ItemCopyStatus.Copied, "column formatting synced");
                    }
                    catch (Exception ex)
                    {
                        result.Add("Field", field.InternalName, "", ItemCopyStatus.Warning, $"formatting sync failed: {ex.Message}");
                    }
                }
                // Lookup fields that already exist still need translation maps.
                if (field.TypeAsString is "Lookup" or "LookupMulti")
                    await RewireLookupSchemaAsync(field, field.SchemaXml, targetLists, result);
                continue;
            }

            if (SkippedFieldTypes.Contains(field.TypeAsString))
            {
                result.Add("Field", field.InternalName, "", ItemCopyStatus.Warning,
                    $"field type {field.TypeAsString} is out of scope (not copied)");
                continue;
            }

            try
            {
                var schema = StripAttrsRegex().Replace(field.SchemaXml, "");

                if (field.TypeAsString is "Lookup" or "LookupMulti")
                {
                    schema = await RewireLookupSchemaAsync(field, schema, targetLists, result);
                    if (schema == null) continue;   // referenced list missing on target
                }

                var created = targetList.Fields.AddFieldAsXml(schema, true, AddFieldOptions.AddFieldInternalNameHint);
                // Column formatting JSON does not survive AddFieldAsXml; push explicitly.
                if (!string.IsNullOrEmpty(field.CustomFormatter))
                {
                    created.CustomFormatter = field.CustomFormatter;
                    created.UpdateAndPushChanges(true);
                }
                await _targetCtx.ExecuteQueryAsync();
                targetFieldNames.Add(field.InternalName);
                result.Add("Field", field.InternalName, field.InternalName, ItemCopyStatus.Copied, field.TypeAsString);
            }
            catch (Exception ex)
            {
                result.Add("Field", field.InternalName, "", ItemCopyStatus.Failed, ex.Message);
            }
        }

        if (options.CopyListSettings)
        {
            targetList.EnableVersioning = sourceList.EnableVersioning;
            if (sourceList.BaseType == BaseType.DocumentLibrary)
                targetList.EnableMinorVersions = sourceList.EnableMinorVersions;
            targetList.EnableFolderCreation = sourceList.EnableFolderCreation;
            if (sourceList.BaseType != BaseType.DocumentLibrary)
                targetList.EnableAttachments = sourceList.EnableAttachments;
            targetList.ContentTypesEnabled = sourceList.ContentTypesEnabled;
            targetList.OnQuickLaunch = sourceList.OnQuickLaunch;
            targetList.Update();
            await _targetCtx.ExecuteQueryAsync();
            result.Add("List", sourceList.Title, options.TargetListTitle, ItemCopyStatus.Copied, "settings applied");
        }

        if (options.CopyViews)
            await CopyViewsAsync(sourceList, targetList, result);

        if (sourceList.ContentTypesEnabled)
            await AttachContentTypesAsync(sourceList, targetList, result);

        return targetList;
    }

    /// <summary>Source list CT string id -> target list CT string id (same Name). Drives per-item CT assignment.</summary>
    public Dictionary<string, string> ContentTypeMap { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Attaches the source list's content types to the target list (matched
    /// on the target WEB by name; DependencyCopier creates missing ones) and
    /// records the id mapping for item-level CT assignment. Idempotent.
    /// </summary>
    private async Task AttachContentTypesAsync(List sourceList, List targetList, CopyResult result)
    {
        _sourceCtx.Load(sourceList.ContentTypes, cts => cts.Include(ct => ct.Name, ct => ct.StringId, ct => ct.Hidden));
        _targetCtx.Load(targetList.ContentTypes, cts => cts.Include(ct => ct.Name, ct => ct.StringId));
        var targetWebCts = _targetCtx.Web.AvailableContentTypes;
        _targetCtx.Load(targetWebCts, cts => cts.Include(ct => ct.Name, ct => ct.StringId));
        await _sourceCtx.ExecuteQueryAsync();
        await _targetCtx.ExecuteQueryAsync();

        var targetListCtsByName = targetList.ContentTypes.AsEnumerable()
            .GroupBy(ct => ct.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().StringId, StringComparer.OrdinalIgnoreCase);

        foreach (var ct in sourceList.ContentTypes.AsEnumerable())
        {
            if (ct.Hidden || ct.StringId.StartsWith("0x012000")) continue;
            if (targetListCtsByName.TryGetValue(ct.Name, out var existingId))
            {
                ContentTypeMap[ct.StringId] = existingId;
                continue;
            }
            var webCt = targetWebCts.AsEnumerable().FirstOrDefault(c => c.Name.Equals(ct.Name, StringComparison.OrdinalIgnoreCase));
            if (webCt == null)
            {
                result.Add("ContentType", ct.Name, "", ItemCopyStatus.Warning, "not available on target web; not attached to list");
                continue;
            }
            try
            {
                var attached = targetList.ContentTypes.AddExistingContentType(webCt);
                _targetCtx.Load(attached, a => a.StringId);
                await _targetCtx.ExecuteQueryAsync();
                ContentTypeMap[ct.StringId] = attached.StringId;
                result.Add("ContentType", ct.Name, ct.Name, ItemCopyStatus.Copied, "attached to target list");
            }
            catch (Exception ex)
            {
                result.Add("ContentType", ct.Name, "", ItemCopyStatus.Warning, $"attach failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Rewires a lookup field's SchemaXml to point at the equivalent list on
    /// the target web: same list id when source and target webs are the same
    /// web, otherwise a target list matched by the source list's TITLE.
    /// Returns null (with a warning recorded) when no equivalent exists.
    /// </summary>
    /// <summary>The List="{guid}" attribute of a lookup field's schema, if present.</summary>
    private static Guid? ParseListId(string schemaXml) =>
        Regex.Match(schemaXml, @"List=""\{?([0-9a-fA-F\-]{36})\}?""") is { Success: true } m
            ? Guid.Parse(m.Groups[1].Value) : null;

    private async Task<string?> RewireLookupSchemaAsync(Field field, string schema, ListCollection targetLists, CopyResult result)
    {
        var match = LookupListAttrRegex().Match(schema);
        if (!match.Success)
        {
            result.Add("Field", field.InternalName, "", ItemCopyStatus.Warning, "lookup field has no resolvable List attribute; skipped");
            return null;
        }
        var sourceListId = Guid.Parse(match.Groups[1].Value);

        // Source referenced list (for its title + show field bookkeeping).
        var sourceLookupList = _sourceCtx.Web.Lists.GetById(sourceListId);
        _sourceCtx.Load(sourceLookupList, l => l.Title, l => l.Id);
        await _sourceCtx.ExecuteQueryAsync();

        // Same web? Point at the very same list. Different web: match by title.
        _sourceCtx.Load(_sourceCtx.Web, w => w.Id);
        _targetCtx.Load(_targetCtx.Web, w => w.Id);
        await _sourceCtx.ExecuteQueryAsync();
        await _targetCtx.ExecuteQueryAsync();

        Guid targetListId;
        if (_sourceCtx.Web.Id == _targetCtx.Web.Id)
        {
            targetListId = sourceListId;
        }
        else
        {
            var candidate = targetLists.AsEnumerable().FirstOrDefault(l =>
                l.Title.Equals(sourceLookupList.Title, StringComparison.OrdinalIgnoreCase));
            if (candidate == null)
            {
                result.Add("Field", field.InternalName, "", ItemCopyStatus.Warning,
                    $"lookup target list '{sourceLookupList.Title}' not found on target web; field skipped (copy that list first)");
                return null;
            }
            targetListId = candidate.Id;
        }

        var showField = Regex.Match(schema, @"ShowField=""([^""]+)""") is { Success: true } sf ? sf.Groups[1].Value : "Title";
        LookupFields.Add((field.InternalName, sourceListId, targetListId, showField));

        schema = LookupListAttrRegex().Replace(schema, $"List=\"{{{targetListId}}}\"");
        schema = WebIdAttrRegex().Replace(schema, "");   // let the target web own it
        return schema;
    }

    private async Task CopyViewsAsync(List sourceList, List targetList, CopyResult result)
    {
        _targetCtx.Load(targetList.Views, vs => vs.Include(v => v.Title));
        await _targetCtx.ExecuteQueryAsync();
        var targetViewTitles = new HashSet<string>(targetList.Views.AsEnumerable().Select(v => v.Title), StringComparer.OrdinalIgnoreCase);

        foreach (var view in sourceList.Views)
        {
            if (view.Hidden || view.PersonalView) continue;
            if (targetViewTitles.Contains(view.Title))
            {
                result.Add("View", view.Title, view.Title, ItemCopyStatus.Skipped, "already exists");
                continue;
            }

            try
            {
                var created = targetList.Views.Add(new ViewCreationInformation
                {
                    Title = view.Title,
                    Query = view.ViewQuery,
                    RowLimit = view.RowLimit,
                    Paged = view.Paged,
                    PersonalView = false,
                    ViewFields = view.ViewFields.ToArray(),
                    SetAsDefaultView = view.DefaultView,
                });
                // View formatting JSON is not part of ViewCreationInformation.
                if (!string.IsNullOrEmpty(view.CustomFormatter))
                {
                    created.CustomFormatter = view.CustomFormatter;
                    created.Update();
                }
                await _targetCtx.ExecuteQueryAsync();
                result.Add("View", view.Title, view.Title, ItemCopyStatus.Copied,
                    string.IsNullOrEmpty(view.CustomFormatter) ? null : "with view formatting JSON");
            }
            catch (Exception ex)
            {
                result.Add("View", view.Title, "", ItemCopyStatus.Failed, ex.Message);
            }
        }
    }
}

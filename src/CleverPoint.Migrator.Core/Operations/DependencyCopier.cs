using CleverPoint.Migrator.Core.Model;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.Core.Operations;

/// <summary>
/// Auto-detects and copies the schema DEPENDENCIES a list needs on the
/// target web before content flows: site columns referenced by the list's
/// fields and content types attached to the list (created with the SAME
/// content type id so re-runs and deltas are no-ops). Idempotent by design:
/// existing site columns / content types are left alone.
/// </summary>
public class DependencyCopier
{
    private readonly ClientContext _sourceCtx;
    private readonly ClientContext _targetCtx;

    public DependencyCopier(ClientContext sourceCtx, ClientContext targetCtx)
    {
        _sourceCtx = sourceCtx;
        _targetCtx = targetCtx;
    }

    public async Task CopyListDependenciesAsync(List sourceList, CopyOptions options, CopyResult result)
    {
        _sourceCtx.Load(sourceList, l => l.ContentTypesEnabled,
            l => l.ContentTypes.Include(ct => ct.Name, ct => ct.Id, ct => ct.StringId, ct => ct.Hidden, ct => ct.Group,
                ct => ct.Parent.StringId, ct => ct.FieldLinks.Include(fl => fl.Name)),
            l => l.Fields.Include(f => f.InternalName, f => f.SchemaXml, f => f.Hidden, f => f.FromBaseType,
                f => f.Sealed, f => f.TypeAsString, f => f.ReadOnlyField));
        await _sourceCtx.ExecuteQueryAsync();

        // Site columns used by the source list that exist as SITE columns on
        // the source web: ensure equivalents on the target web.
        var sourceWebFields = _sourceCtx.Web.AvailableFields;
        var targetWebFields = _targetCtx.Web.AvailableFields;
        _sourceCtx.Load(sourceWebFields, fs => fs.Include(f => f.InternalName, f => f.SchemaXml, f => f.Group));
        _targetCtx.Load(targetWebFields, fs => fs.Include(f => f.InternalName));
        await _sourceCtx.ExecuteQueryAsync();
        await _targetCtx.ExecuteQueryAsync();

        var targetWebFieldNames = new HashSet<string>(
            targetWebFields.AsEnumerable().Select(f => f.InternalName), StringComparer.OrdinalIgnoreCase);
        var sourceWebFieldsByName = sourceWebFields.AsEnumerable()
            .GroupBy(f => f.InternalName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var listFieldNames = sourceList.Fields.AsEnumerable()
            .Where(f => !f.Hidden && !f.FromBaseType && !f.Sealed)
            .Select(f => f.InternalName).ToList();

        foreach (var name in listFieldNames)
        {
            if (!sourceWebFieldsByName.TryGetValue(name, out var webField)) continue;  // list-local column
            if (string.IsNullOrEmpty(webField.Group) || webField.Group.StartsWith("_")) continue;
            if (targetWebFieldNames.Contains(name))
            {
                result.Add("SiteColumn", name, name, ItemCopyStatus.Skipped, "already on target web");
                continue;
            }
            try
            {
                var schema = System.Text.RegularExpressions.Regex.Replace(
                    webField.SchemaXml, @"\s(SourceID|Version|ColName|RowOrdinal)=""[^""]*""", "");
                _targetCtx.Web.Fields.AddFieldAsXml(schema, false, AddFieldOptions.AddFieldInternalNameHint);
                await _targetCtx.ExecuteQueryAsync();
                targetWebFieldNames.Add(name);
                result.Add("SiteColumn", name, name, ItemCopyStatus.Copied, webField.Group);
            }
            catch (Exception ex)
            {
                result.Add("SiteColumn", name, "", ItemCopyStatus.Warning, $"site column copy failed: {ex.Message}");
            }
        }

        if (!sourceList.ContentTypesEnabled) return;

        // Content types attached to the list: ensure on the target WEB with
        // the same id (the schema copier then attaches them to the list).
        var targetWebCts = _targetCtx.Web.AvailableContentTypes;
        _targetCtx.Load(targetWebCts, cts => cts.Include(ct => ct.StringId, ct => ct.Name));
        await _targetCtx.ExecuteQueryAsync();
        var targetCtIds = new HashSet<string>(targetWebCts.AsEnumerable().Select(ct => ct.StringId), StringComparer.OrdinalIgnoreCase);
        var targetCtNames = new HashSet<string>(targetWebCts.AsEnumerable().Select(ct => ct.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var ct in sourceList.ContentTypes.AsEnumerable())
        {
            if (ct.Hidden || ct.StringId.StartsWith("0x0120")) continue;   // folder CTs ship with the list
            // List-scoped CT ids derive from the site CT id; the site-level
            // parent is what must exist on the target web.
            var siteCtId = ct.Parent.StringId;
            if (targetCtIds.Contains(siteCtId) || targetCtNames.Contains(ct.Name))
            {
                result.Add("ContentType", ct.Name, ct.Name, ItemCopyStatus.Skipped, "already on target web");
                continue;
            }
            if (siteCtId is "0x01" or "0x0101") continue;   // base types always exist

            try
            {
                var created = _targetCtx.Web.ContentTypes.Add(new ContentTypeCreationInformation
                {
                    Id = siteCtId,
                    Name = ct.Name,
                    Group = string.IsNullOrEmpty(ct.Group) ? "Migrated Content Types" : ct.Group,
                });
                await _targetCtx.ExecuteQueryAsync();

                // Field links: attach site columns the CT uses (best effort).
                foreach (var fl in ct.FieldLinks.AsEnumerable())
                {
                    // Inherited base links exist on every CT already.
                    if (fl.Name is "ContentType" or "Title") continue;
                    if (!targetWebFieldNames.Contains(fl.Name)) continue;
                    try
                    {
                        var field = _targetCtx.Web.AvailableFields.GetByInternalNameOrTitle(fl.Name);
                        created.FieldLinks.Add(new FieldLinkCreationInformation { Field = field });
                        created.Update(false);
                        await _targetCtx.ExecuteQueryAsync();
                    }
                    catch (Exception ex)
                    {
                        result.Add("ContentType", ct.Name, "", ItemCopyStatus.Warning, $"field link {fl.Name}: {ex.Message}");
                    }
                }
                targetCtIds.Add(siteCtId);
                targetCtNames.Add(ct.Name);
                result.Add("ContentType", ct.Name, ct.Name, ItemCopyStatus.Copied, siteCtId);
            }
            catch (Exception ex)
            {
                result.Add("ContentType", ct.Name, "", ItemCopyStatus.Warning, $"content type copy failed: {ex.Message}");
            }
        }
    }
}

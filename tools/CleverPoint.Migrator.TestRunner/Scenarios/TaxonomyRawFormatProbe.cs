using CleverPoint.Migrator.Core.Auth;
using CleverPoint.Migrator.Core.Csom;
using Microsoft.SharePoint.Client;
using Microsoft.SharePoint.Client.Taxonomy;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Probe: what does SharePoint ACTUALLY store for a taxonomy column?
///
/// The Migration API import writes straight to the content DB and does not fire the taxonomy event
/// receivers, so a package has to write the stored representation itself. This prints it: the taxonomy
/// column's raw value (a lookup into the site's hidden TaxonomyHiddenList -- "WssId;#Label|TermGuid"),
/// the companion hidden Note field's raw value, and the WssId that GetWssIdsOfTerm reports for the term.
///
/// Read the output, then encode exactly that in the manifest. Do not guess.
/// </summary>
public static class TaxonomyRawFormatProbe
{
    private const string TargetSite = "https://cleverpointlab.sharepoint.com/sites/intranet";
    private const string Library = "TaxMatrix-Lib-crossXT";

    public static async Task RunAsync()
    {
        var target = new SpConnection(TargetSite, new CertTokenProvider(Program.TargetCreds));
        using var ctx = target.CreateContext();

        var list = ctx.Web.Lists.GetByTitle(Library);
        ctx.Load(list, l => l.Title);
        await ctx.ExecuteQueryAsync();

        // The columns and their companion note fields.
        var columns = new[] { "TaxSiteSingle", "TaxSiteMulti", "TaxGlobalSingle", "TaxGlobalMulti" };
        var taxFields = new Dictionary<string, TaxonomyField>();
        foreach (var c in columns)
        {
            var tf = ctx.CastTo<TaxonomyField>(list.Fields.GetByInternalNameOrTitle(c));
            ctx.Load(tf, f => f.Id, f => f.TextField, f => f.SspId, f => f.TermSetId, f => f.AllowMultipleValues);
            taxFields[c] = tf;
        }
        await ctx.ExecuteQueryAsync();

        var noteNames = new Dictionary<string, string>();
        foreach (var c in columns)
        {
            var note = list.Fields.GetById(taxFields[c].TextField);
            ctx.Load(note, f => f.InternalName, f => f.Title);
            await ctx.ExecuteQueryAsync();
            noteNames[c] = note.InternalName;
            Console.WriteLine($"  {c}: fieldId={taxFields[c].Id}  noteField='{note.InternalName}' (title '{note.Title}')");
            Program.Check($"raw-probe: {c} note field internal name == field GUID with no dashes",
                string.Equals(note.InternalName, taxFields[c].Id.ToString("N"), StringComparison.OrdinalIgnoreCase),
                $"{note.InternalName} vs {taxFields[c].Id:N}");
        }

        // Read an existing (already correctly copied) item and dump its stored representation.
        var items = list.GetItems(new CamlQuery { ViewXml = "<View><RowLimit>1</RowLimit></View>" });
        ctx.Load(items);
        await ctx.ExecuteQueryAsync();
        var item = items.FirstOrDefault();
        Program.Check("raw-probe: found an item to inspect", item != null, Library);
        if (item == null) return;

        Console.WriteLine();
        foreach (var c in columns)
        {
            var value = item.FieldValues.GetValueOrDefault(c);
            var note = item.FieldValues.GetValueOrDefault(noteNames[c]);

            string rendered;
            if (value is TaxonomyFieldValueCollection col)
                rendered = string.Join(" ;# ", col.Select(v => $"[WssId={v.WssId} Label='{v.Label}' Guid={v.TermGuid}]"));
            else if (value is TaxonomyFieldValue single)
                rendered = $"[WssId={single.WssId} Label='{single.Label}' Guid={single.TermGuid}]";
            else
                rendered = $"<{value?.GetType().Name ?? "null"}> {value}";

            Console.WriteLine($"  {c}");
            Console.WriteLine($"      taxonomy field : {rendered}");
            Console.WriteLine($"      NOTE field raw : {note ?? "<null>"}");
        }

        // The WssId is an item id in the site's hidden TaxonomyHiddenList. Dump it: this is the lookup
        // table the manifest's taxonomy value points into, and the thing the import cannot populate.
        Console.WriteLine();
        Console.WriteLine("  --- TaxonomyHiddenList (WssId -> term) ---");
        var hidden = ctx.Web.Lists.GetByTitle("TaxonomyHiddenList");
        var hiddenItems = hidden.GetItems(new CamlQuery { ViewXml = "<View><RowLimit>50</RowLimit></View>" });
        ctx.Load(hiddenItems);
        await ctx.ExecuteQueryAsync();
        foreach (var h in hiddenItems)
            Console.WriteLine($"      WssId={h.Id,-4} Term='{h.FieldValues.GetValueOrDefault("Title")}' "
                            + $"IdForTerm={h.FieldValues.GetValueOrDefault("IdForTerm")} "
                            + $"IdForTermSet={h.FieldValues.GetValueOrDefault("IdForTermSet")}");
        Program.Check("raw-probe: TaxonomyHiddenList is readable", hiddenItems.Count > 0, $"{hiddenItems.Count} row(s)");
    }
}

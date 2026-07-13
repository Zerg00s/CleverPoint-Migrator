using CleverPoint.Migrator.Core.Auth;
using CleverPoint.Migrator.Core.Csom;
using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using Microsoft.SharePoint.Client;
using Microsoft.SharePoint.Client.Taxonomy;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// CROSS-TENANT copy of a list carrying BOTH flavours of managed metadata:
///   - a column bound to a TENANT (global) term set   -> group 'Clever' / 'Document Category'
///   - a column bound to a SITE-COLLECTION term set    -> group 'Demo Site Term Group' / 'One'
///
/// The two tenants have different term stores, so none of the source GUIDs (SspId, term set, term)
/// exist on the target. Before TermStoreCopier, the target columns were bound to the SOURCE SspId
/// (a dead column) and every item write failed with "The given guid does not exist in the term store".
///
/// This asserts the whole chain: term sets replicated into the right target group (the site-collection
/// one under the TARGET site, not the source's), columns bound to the TARGET store, and item values
/// landing with the right labels and resolvable term GUIDs.
/// </summary>
public static class CrossTenantTaxonomyTest
{
    private const string SourceSite = "https://gocleverpointcom.sharepoint.com/sites/DemoLargeSite";
    private const string TargetSite = "https://cleverpointlab.sharepoint.com/sites/intranet";
    private const string ListTitle  = "TaxonomyMigrationTest";

    private const string SiteScopedColumn = "Taxonomy_x0020_Column_x0020_Site";
    private const string GlobalColumn     = "Taxonomy_x0020_Global_x0020_Term";

    public static async Task RunAsync()
    {
        var source = new SpConnection(SourceSite, new CertTokenProvider(Program.SourceCreds));
        var target = new SpConnection(TargetSite, new CertTokenProvider(Program.TargetCreds));

        var options = new CopyOptions
        {
            TargetListTitle = ListTitle,
            TargetListUrl = $"Lists/{ListTitle}",
            CopyContent = true,
            CopyListSettings = true,
            CopyViews = true,
            ExistingMode = ExistingItemMode.Overwrite,   // the target list already exists (with dead columns)
        };

        var result = await CopyEngine.CopyListAsync(source, target, ListTitle, options);

        foreach (var r in result.Records.Where(r => r.ItemType is "TermStore" or "TermGroup" or "TermSet" or "Term" or "Field"))
            Console.WriteLine($"  [{r.Status}] {r.ItemType} {r.SourcePath}: {r.Message}");

        var failed = result.Records.Where(r => r.Status == ItemCopyStatus.Failed).ToList();
        foreach (var f in failed)
            Console.WriteLine($"  [FAILED] {f.ItemType} {f.SourcePath}: {f.Message}");
        Program.Check("xt-tax: copy produced no failures", failed.Count == 0, $"{failed.Count} failure(s)");

        // ---- Verify on the TARGET tenant ----
        using var tctx = target.CreateContext();
        var session = TaxonomySession.GetTaxonomySession(tctx);
        var store = session.GetDefaultSiteCollectionTermStore();
        tctx.Load(store, s => s.Id);
        var site = tctx.Site;
        tctx.Load(site, s => s.Id);
        await tctx.ExecuteQueryAsync();

        var list = tctx.Web.Lists.GetByTitle(ListTitle);
        tctx.Load(list, l => l.ItemCount);
        await tctx.ExecuteQueryAsync();

        Program.Check("xt-tax: items copied", list.ItemCount > 0, $"target ItemCount={list.ItemCount}");

        foreach (var (column, expectSiteCollectionGroup) in new[]
                 {
                     (SiteScopedColumn, true),
                     (GlobalColumn, false),
                 })
        {
            var tax = tctx.CastTo<TaxonomyField>(list.Fields.GetByInternalNameOrTitle(column));
            tctx.Load(tax, f => f.SspId, f => f.TermSetId, f => f.InternalName);
            await tctx.ExecuteQueryAsync();

            Program.Check($"xt-tax: {column} bound to TARGET term store",
                tax.SspId == store.Id, $"SspId={tax.SspId} store={store.Id}");

            var ts = store.GetTermSet(tax.TermSetId);
            tctx.Load(ts, t => t.Id, t => t.Name, t => t.Group.Name, t => t.Group.Id, t => t.Group.IsSiteCollectionGroup);
            await tctx.ExecuteQueryAsync();

            Program.Check($"xt-tax: {column} term set resolves on target",
                ts.ServerObjectIsNull != true, $"termSet={tax.TermSetId}");

            if (ts.ServerObjectIsNull == true) continue;

            Program.Check($"xt-tax: {column} term set in the right group scope",
                ts.Group.IsSiteCollectionGroup == expectSiteCollectionGroup,
                $"'{ts.Name}' in '{ts.Group.Name}' (siteCollectionGroup={ts.Group.IsSiteCollectionGroup}, expected {expectSiteCollectionGroup})");

            // A site-collection term set must live under the TARGET site's own group, never the source's.
            if (expectSiteCollectionGroup)
            {
                var scGroup = store.GetSiteCollectionGroup(site, false);
                tctx.Load(scGroup, g => g.Id, g => g.Name);
                await tctx.ExecuteQueryAsync();
                Program.Check($"xt-tax: {column} uses the TARGET site's site-collection group",
                    scGroup.ServerObjectIsNull != true && ts.Group.Id == scGroup.Id,
                    $"group={ts.Group.Name} ({ts.Group.Id})");
            }
        }

        // ---- Item values ----
        var items = list.GetItems(new CamlQuery { ViewXml = "<View><RowLimit>50</RowLimit></View>" });
        tctx.Load(items);
        await tctx.ExecuteQueryAsync();

        foreach (var item in items)
        {
            foreach (var column in new[] { SiteScopedColumn, GlobalColumn })
            {
                var value = item.FieldValues.GetValueOrDefault(column) as TaxonomyFieldValue;
                Program.Check($"xt-tax: item {item.Id} '{column}' has a value",
                    value != null && !string.IsNullOrEmpty(value.Label), value?.Label ?? "<null>");
                if (value == null || string.IsNullOrEmpty(value.TermGuid)) continue;

                // The stored GUID must actually resolve in the TARGET store -- a dangling GUID is
                // exactly the failure mode this whole feature exists to prevent.
                var term = store.GetTerm(Guid.Parse(value.TermGuid));
                tctx.Load(term, t => t.Id, t => t.Name);
                await tctx.ExecuteQueryAsync();
                Program.Check($"xt-tax: item {item.Id} '{column}' term resolves in target store",
                    term.ServerObjectIsNull != true, $"'{value.Label}' -> {value.TermGuid}");
                if (term.ServerObjectIsNull != true)
                    Program.Check($"xt-tax: item {item.Id} '{column}' label matches term",
                        string.Equals(term.Name, value.Label, StringComparison.Ordinal), $"{term.Name} == {value.Label}");
            }
        }
    }
}

using CleverPoint.Migrator.Core.Auth;
using CleverPoint.Migrator.Core.Csom;
using CleverPoint.Migrator.Core.MigrationApi;
using CleverPoint.Migrator.Core.Model;
using Microsoft.SharePoint.Client;
using Microsoft.SharePoint.Client.Taxonomy;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// The MIGRATION API engine carrying managed metadata, which it could not do before: the import bypasses
/// the taxonomy event receivers, so the package has to write the stored representation itself (the
/// TaxonomyHiddenList lookup + the hidden note field), with a WssId seeded per term.
///
/// Runs the API engine over a library with all four taxonomy shapes (site-scoped/global x single/multi),
/// same-tenant AND cross-tenant, and asserts the values land exactly as the Classic engine leaves them:
/// right label, right term GUID, resolvable in the target term store, and a live WssId -- not the -1
/// "unresolved" placeholder, which is what a naive package produces.
/// </summary>
public static class TaxonomyApiEngineTest
{
    private const string SourceSite = "https://gocleverpointcom.sharepoint.com/sites/DemoLargeSite";
    private const string SameTenantOtherSite = "https://gocleverpointcom.sharepoint.com/sites/delete-me-please-test";
    private const string CrossTenantSite = "https://cleverpointlab.sharepoint.com/sites/intranet";
    private const string ColdTargetSite = "https://cleverpointlab.sharepoint.com/sites/Migrator-test";
    private const string SrcLib = "TaxMatrix-SrcLib";

    /// <summary>The source terms, by label. Used to assert a cold target really is cold.</summary>
    private static readonly string[] TermLabels = { "Apple", "Orange", "Consultation", "Meeting" };

    private static readonly (string Name, bool SiteScoped, bool Multi)[] Columns =
    {
        ("TaxSiteSingle",   true,  false),
        ("TaxSiteMulti",    true,  true),
        ("TaxGlobalSingle", false, false),
        ("TaxGlobalMulti",  false, true),
    };

    public static async Task RunAsync()
    {
        var source = new SpConnection(SourceSite, new CertTokenProvider(Program.SourceCreds));

        var destinations = new (string Label, SpConnection Conn)[]
        {
            ("same-tenant-other-sc", new SpConnection(SameTenantOtherSite, new CertTokenProvider(Program.SourceCreds))),
            ("cross-tenant",         new SpConnection(CrossTenantSite,     new CertTokenProvider(Program.TargetCreds))),
            // A site that has NEVER seen these terms: its TaxonomyHiddenList has no rows for them, so
            // there is no WssId to point at. This is the case the seeding step exists for, and the one a
            // package that just writes "-1;#Label|Guid" gets wrong. Asserted cold before the copy runs.
            ("cross-tenant-cold",    new SpConnection(ColdTargetSite,      new CertTokenProvider(Program.TargetCreds))),
        };

        foreach (var (label, target) in destinations)
        {
            var title = $"TaxApi-{(label == "cross-tenant" ? "XT" : label == "cross-tenant-cold" ? "Cold" : "SC")}";
            var tag = $"api/{label}";

            using (var tctx = target.CreateContext())
                await TestAssets.DeleteIfExistsAsync(tctx, title);

            if (label == "cross-tenant-cold")
                await AssertColdAsync(target, tag);

            var options = new CopyOptions
            {
                TargetListTitle = title,
                TargetListUrl = title,
                CopyContent = true,
                CopyListSettings = true,
                CopyViews = true,
            };

            var engine = new MigrationApiEngine(source, target);
            engine.OnProgress += m => Console.WriteLine($"    [api] {m}");

            CopyResult result;
            try
            {
                result = await engine.CopyLibraryAsync(SrcLib, options);
            }
            catch (Exception ex)
            {
                Program.Check($"tax-api [{tag}]: copy completed", false, ex.Message);
                continue;
            }

            var failed = result.Records.Where(r => r.Status == ItemCopyStatus.Failed).ToList();
            foreach (var f in failed)
                Console.WriteLine($"    [FAILED] {f.ItemType} {f.SourcePath}: {f.Message}");
            Program.Check($"tax-api [{tag}]: copy produced no failures", failed.Count == 0, $"{failed.Count} failure(s)");

            await VerifyAsync(target, title, tag);
        }
    }

    /// <summary>
    /// Reports whether the target's TaxonomyHiddenList already has rows for our terms.
    ///
    /// When it does NOT, the WssIds the copy ends up with can only have come from the seeding step -- that
    /// is the strong proof, and it is asserted. But seeding is what warms the site, so this only holds the
    /// FIRST time: on a re-run the terms are legitimately already indexed. Failing then would be a false
    /// red, so a warm target is reported and skipped rather than failed. (To re-prove cold-start, point
    /// ColdTargetSite at a site collection that has never received these terms.)
    /// </summary>
    private static async Task AssertColdAsync(SpConnection target, string tag)
    {
        using var ctx = target.CreateContext();
        var hidden = ctx.Web.Lists.GetByTitle("TaxonomyHiddenList");
        var rows = hidden.GetItems(new CamlQuery { ViewXml = "<View><RowLimit>500</RowLimit></View>" });
        ctx.Load(rows);
        await ctx.ExecuteQueryAsync();

        var present = rows.AsEnumerable()
            .Select(r => r.FieldValues.GetValueOrDefault("Title")?.ToString() ?? "")
            .Where(t => TermLabels.Contains(t, StringComparer.OrdinalIgnoreCase))
            .Distinct()
            .ToList();

        if (present.Count == 0)
            Program.Check($"tax-api [{tag}]: target is COLD -- no WssId row exists for any of our terms",
                true, $"{rows.Count} unrelated row(s); the WssIds below can only come from seeding");
        else
            Console.WriteLine($"    [note] {tag}: target already warm from an earlier run "
                              + $"({string.Join(", ", present)} indexed) -- cold-start not re-proven this run");
    }

    private static async Task VerifyAsync(SpConnection target, string listTitle, string tag)
    {
        using var tctx = target.CreateContext();
        var session = TaxonomySession.GetTaxonomySession(tctx);
        var store = session.GetDefaultSiteCollectionTermStore();
        tctx.Load(store, s => s.Id);
        tctx.Load(tctx.Site, s => s.Id);

        var list = tctx.Web.Lists.GetByTitle(listTitle);
        tctx.Load(list, l => l.ItemCount);
        await tctx.ExecuteQueryAsync();

        var scGroup = store.GetSiteCollectionGroup(tctx.Site, false);
        tctx.Load(scGroup, g => g.Id, g => g.Name);
        await tctx.ExecuteQueryAsync();

        Program.Check($"tax-api [{tag}]: files copied", list.ItemCount >= 2, $"ItemCount={list.ItemCount}");

        // Columns must be bound to the TARGET store, in the right group scope.
        foreach (var (name, siteScoped, _) in Columns)
        {
            var tax = tctx.CastTo<TaxonomyField>(list.Fields.GetByInternalNameOrTitle(name));
            tctx.Load(tax, f => f.SspId, f => f.TermSetId);
            await tctx.ExecuteQueryAsync();

            Program.Check($"tax-api [{tag}]: {name} bound to TARGET term store",
                tax.SspId == store.Id, $"SspId={tax.SspId} store={store.Id}");

            var ts = store.GetTermSet(tax.TermSetId);
            tctx.Load(ts, t => t.Name, t => t.Group.Id, t => t.Group.IsSiteCollectionGroup);
            await tctx.ExecuteQueryAsync();
            if (ts.ServerObjectIsNull == true)
            {
                Program.Check($"tax-api [{tag}]: {name} term set resolves", false, $"{tax.TermSetId}");
                continue;
            }
            Program.Check($"tax-api [{tag}]: {name} group scope is {(siteScoped ? "site-collection" : "tenant")}",
                ts.Group.IsSiteCollectionGroup == siteScoped, $"'{ts.Name}'");
            if (siteScoped)
                Program.Check($"tax-api [{tag}]: {name} uses the TARGET site's site-collection group",
                    scGroup.ServerObjectIsNull != true && ts.Group.Id == scGroup.Id, $"'{ts.Name}'");
        }

        // The values themselves.
        var items = list.GetItems(new CamlQuery { ViewXml = "<View Scope='RecursiveAll'><RowLimit>50</RowLimit></View>" });
        tctx.Load(items);
        await tctx.ExecuteQueryAsync();

        var checkedAny = false;
        var sawFolder = false;
        foreach (var item in items)
        {
            // Folders carry taxonomy too, and the package writes theirs the same way. Do NOT skip them:
            // that is precisely where a manifest that only fills in <Fields> for files would fall down.
            if (item.FieldValues.GetValueOrDefault("FSObjType")?.ToString() == "1") sawFolder = true;

            foreach (var (name, _, multi) in Columns)
            {
                var raw = item.FieldValues.GetValueOrDefault(name);
                if (raw == null)
                {
                    Program.Check($"tax-api [{tag}]: item {item.Id} {name} has a value", false, "<null> -- value was DROPPED");
                    continue;
                }

                var values = multi
                    ? ((TaxonomyFieldValueCollection)raw).ToList()
                    : new List<TaxonomyFieldValue> { (TaxonomyFieldValue)raw };

                Program.Check($"tax-api [{tag}]: item {item.Id} {name} has value(s)", values.Count > 0,
                    string.Join(", ", values.Select(v => v.Label)));
                Program.Check($"tax-api [{tag}]: item {item.Id} {name} multi carries BOTH terms",
                    !multi || values.Count == 2, $"{values.Count} value(s)");

                foreach (var v in values)
                {
                    checkedAny = true;

                    // A WssId of -1 means the lookup into TaxonomyHiddenList never resolved: the column
                    // renders but is broken for filtering/refinement. This is the failure the seeding
                    // step exists to prevent, so assert it explicitly.
                    Program.Check($"tax-api [{tag}]: item {item.Id} {name} '{v.Label}' has a real WssId",
                        v.WssId > 0, $"WssId={v.WssId}");

                    var term = store.GetTerm(Guid.Parse(v.TermGuid));
                    tctx.Load(term, t => t.Id, t => t.Name);
                    await tctx.ExecuteQueryAsync();

                    var resolves = term.ServerObjectIsNull != true;
                    Program.Check($"tax-api [{tag}]: item {item.Id} {name} '{v.Label}' resolves in target store",
                        resolves, v.TermGuid);
                    if (resolves)
                        Program.Check($"tax-api [{tag}]: item {item.Id} {name} '{v.Label}' label matches term",
                            string.Equals(term.Name, v.Label, StringComparison.Ordinal), $"{term.Name} == {v.Label}");
                }
            }
        }
        Program.Check($"tax-api [{tag}]: taxonomy values were actually checked", checkedAny, "");
        Program.Check($"tax-api [{tag}]: the tagged FOLDER came across", sawFolder, TaxonomyMatrixTest.TaggedFolder);

        // No seed leftovers.
        var leftovers = items.AsEnumerable().Count(i =>
        {
            var leaf = i.FieldValues.GetValueOrDefault("FileLeafRef");
            return leaf != null && leaf.ToString()!.StartsWith("_taxonomy-seed-", StringComparison.OrdinalIgnoreCase);
        });
        Program.Check($"tax-api [{tag}]: the taxonomy seed item was cleaned up", leftovers == 0, $"{leftovers} left");
    }
}

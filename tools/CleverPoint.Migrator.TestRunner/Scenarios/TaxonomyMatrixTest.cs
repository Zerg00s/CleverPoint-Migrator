using CleverPoint.Migrator.Core.Auth;
using CleverPoint.Migrator.Core.Csom;
using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using Microsoft.SharePoint.Client;
using Microsoft.SharePoint.Client.Taxonomy;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// The full managed-metadata matrix:
///
///   destination : same site collection | same tenant / DIFFERENT site collection | cross-tenant
///   container   : list | library
///   term scope  : site-collection term set | tenant (global) term set
///   cardinality : single | multi
///
/// The three destinations are three genuinely different problems:
///   - same site collection  -> same term store, same site: every GUID is already valid, nothing to do.
///   - same tenant, other SC -> same term store, so a TENANT term set carries over untouched -- but a
///                              SITE-COLLECTION term set belongs to the source site and is unusable from
///                              the target site, so it must be re-homed under the target's own group.
///                              Its old GUIDs are still taken in that same store, so the terms get NEW
///                              ones and the item values must be remapped.
///   - cross-tenant          -> different term store: nothing exists there, everything is replicated.
///
/// Each case asserts the binding lands in the TARGET store, the term set sits in the correct group
/// (and, when site-scoped, under the TARGET site's own group), and every copied value resolves back
/// to a real term with the right label.
/// </summary>
public static class TaxonomyMatrixTest
{
    private const string SourceSite = "https://gocleverpointcom.sharepoint.com/sites/DemoLargeSite";
    private const string SameTenantOtherSite = "https://gocleverpointcom.sharepoint.com/sites/delete-me-please-test";
    private const string CrossTenantSite = "https://cleverpointlab.sharepoint.com/sites/intranet";

    private const string SrcList = "TaxMatrix-SrcList";
    private const string SrcLib = "TaxMatrix-SrcLib";
    public const string TaggedFolder = "TaggedFolder";

    // The reference list that already carries one site-scoped and one global column.
    private const string ReferenceList = "TaxonomyMigrationTest";

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

        // ---- Learn the two source term sets from the reference list, and pick terms from each ----
        Guid siteTermSetId, globalTermSetId, sourceSspId;
        List<(string Label, Guid Id)> siteTerms, globalTerms;

        using (var sctx = source.CreateContext())
        {
            var session = TaxonomySession.GetTaxonomySession(sctx);
            var store = session.GetDefaultSiteCollectionTermStore();
            sctx.Load(store, s => s.Id);
            var refList = sctx.Web.Lists.GetByTitle(ReferenceList);
            var siteCol = sctx.CastTo<TaxonomyField>(refList.Fields.GetByInternalNameOrTitle("Taxonomy_x0020_Column_x0020_Site"));
            var globalCol = sctx.CastTo<TaxonomyField>(refList.Fields.GetByInternalNameOrTitle("Taxonomy_x0020_Global_x0020_Term"));
            sctx.Load(siteCol, f => f.TermSetId);
            sctx.Load(globalCol, f => f.TermSetId);
            await sctx.ExecuteQueryAsync();

            sourceSspId = store.Id;
            siteTermSetId = siteCol.TermSetId;
            globalTermSetId = globalCol.TermSetId;
            siteTerms = await ReadTermsAsync(sctx, store, siteTermSetId);
            globalTerms = await ReadTermsAsync(sctx, store, globalTermSetId);
        }

        Program.Check("tax-matrix: source site-scoped term set found", siteTerms.Count >= 2,
            $"{siteTermSetId} -> {siteTerms.Count} term(s)");
        Program.Check("tax-matrix: source global term set found", globalTerms.Count >= 2,
            $"{globalTermSetId} -> {globalTerms.Count} term(s)");
        Console.WriteLine($"  source term store {sourceSspId}");
        Console.WriteLine($"  site-scoped set {siteTermSetId}: {string.Join(", ", siteTerms.Select(t => t.Label))}");
        Console.WriteLine($"  global set      {globalTermSetId}: {string.Join(", ", globalTerms.Select(t => t.Label))}");

        // ---- Provision the source list and library with all four columns, tagged ----
        await ProvisionSourceAsync(source, siteTermSetId, globalTermSetId, siteTerms, globalTerms);

        // ---- Run the matrix ----
        var destinations = new (string Label, SpConnection Conn, bool ExpectSameSite)[]
        {
            ("same-site-collection", source, true),
            ("same-tenant-other-sc", new SpConnection(SameTenantOtherSite, new CertTokenProvider(Program.SourceCreds)), false),
            ("cross-tenant",         new SpConnection(CrossTenantSite,     new CertTokenProvider(Program.TargetCreds)), false),
        };

        foreach (var (label, target, expectSameSite) in destinations)
        {
            foreach (var (sourceTitle, isLibrary) in new[] { (SrcList, false), (SrcLib, true) })
            {
                var targetTitle = $"TaxMatrix-{(isLibrary ? "Lib" : "List")}-{(expectSameSite ? "Same" : label.Split('-')[0])}"
                                  + (label == "same-tenant-other-sc" ? "SC" : label == "cross-tenant" ? "XT" : "");
                var tag = $"{label}/{(isLibrary ? "library" : "list")}";

                using (var tctx = target.CreateContext())
                    await TestAssets.DeleteIfExistsAsync(tctx, targetTitle);

                var options = new CopyOptions
                {
                    TargetListTitle = targetTitle,
                    TargetListUrl = isLibrary ? targetTitle : $"Lists/{targetTitle}",
                    CopyContent = true,
                    CopyListSettings = true,
                    CopyViews = true,
                };

                CopyResult result;
                try
                {
                    result = await CopyEngine.CopyListAsync(source, target, sourceTitle, options);
                }
                catch (Exception ex)
                {
                    Program.Check($"tax-matrix [{tag}]: copy completed", false, ex.Message);
                    continue;
                }

                var failed = result.Records.Where(r => r.Status == ItemCopyStatus.Failed).ToList();
                foreach (var f in failed)
                    Console.WriteLine($"    [FAILED] {f.ItemType} {f.SourcePath}: {f.Message}");
                Program.Check($"tax-matrix [{tag}]: copy produced no failures", failed.Count == 0,
                    $"{failed.Count} failure(s)");

                await VerifyAsync(target, targetTitle, tag, isLibrary, expectSameSite);
            }
        }
    }

    /// <summary>Every taxonomy column on the target binds to the target store, in the right group, with resolvable values.</summary>
    private static async Task VerifyAsync(SpConnection target, string listTitle, string tag, bool isLibrary, bool expectSameSite)
    {
        using var tctx = target.CreateContext();
        var session = TaxonomySession.GetTaxonomySession(tctx);
        var store = session.GetDefaultSiteCollectionTermStore();
        tctx.Load(store, s => s.Id);
        tctx.Load(tctx.Site, s => s.Id);
        var list = tctx.Web.Lists.GetByTitle(listTitle);
        tctx.Load(list, l => l.ItemCount);
        await tctx.ExecuteQueryAsync();

        // The target site's OWN site-collection group: site-scoped term sets must land here.
        var scGroup = store.GetSiteCollectionGroup(tctx.Site, false);
        tctx.Load(scGroup, g => g.Id, g => g.Name);
        await tctx.ExecuteQueryAsync();

        Program.Check($"tax-matrix [{tag}]: content copied", list.ItemCount > 0, $"ItemCount={list.ItemCount}");

        foreach (var (name, siteScoped, _) in Columns)
        {
            var tax = tctx.CastTo<TaxonomyField>(list.Fields.GetByInternalNameOrTitle(name));
            tctx.Load(tax, f => f.SspId, f => f.TermSetId);
            await tctx.ExecuteQueryAsync();

            Program.Check($"tax-matrix [{tag}]: {name} bound to TARGET term store",
                tax.SspId == store.Id, $"SspId={tax.SspId} store={store.Id}");

            var ts = store.GetTermSet(tax.TermSetId);
            tctx.Load(ts, t => t.Id, t => t.Name, t => t.Group.Id, t => t.Group.Name, t => t.Group.IsSiteCollectionGroup);
            await tctx.ExecuteQueryAsync();

            if (ts.ServerObjectIsNull == true)
            {
                Program.Check($"tax-matrix [{tag}]: {name} term set resolves in target store", false, $"{tax.TermSetId}");
                continue;
            }
            Program.Check($"tax-matrix [{tag}]: {name} term set resolves in target store", true,
                $"'{ts.Name}' in '{ts.Group.Name}'");

            Program.Check($"tax-matrix [{tag}]: {name} group scope is {(siteScoped ? "site-collection" : "tenant")}",
                ts.Group.IsSiteCollectionGroup == siteScoped,
                $"siteCollectionGroup={ts.Group.IsSiteCollectionGroup}");

            // The decisive one: a site-scoped term set must live under the TARGET site's own group.
            // Same tenant, different site collection is where the old code silently kept pointing at
            // the SOURCE site's group -- resolvable in the store, but unusable from this site.
            if (siteScoped)
                Program.Check($"tax-matrix [{tag}]: {name} uses the TARGET site's site-collection group",
                    scGroup.ServerObjectIsNull != true && ts.Group.Id == scGroup.Id,
                    $"group='{ts.Group.Name}' expected='{(scGroup.ServerObjectIsNull == true ? "<none>" : scGroup.Name)}'");
        }

        // ---- Values ----
        var items = list.GetItems(new CamlQuery { ViewXml = "<View Scope='RecursiveAll'><RowLimit>50</RowLimit></View>" });
        tctx.Load(items);
        await tctx.ExecuteQueryAsync();

        var checkedAny = false;
        var sawFolder = false;
        foreach (var item in items)
        {
            var isFolder = item.FieldValues.GetValueOrDefault("FSObjType")?.ToString() == "1";
            if (isFolder) sawFolder = true;

            foreach (var (name, _, multi) in Columns)
            {
                var raw = item.FieldValues.GetValueOrDefault(name);
                if (raw == null)
                {
                    Program.Check($"tax-matrix [{tag}]: item {item.Id} {name} has a value", false, "<null>");
                    continue;
                }

                var values = multi
                    ? ((TaxonomyFieldValueCollection)raw).ToList()
                    : new List<TaxonomyFieldValue> { (TaxonomyFieldValue)raw };

                Program.Check($"tax-matrix [{tag}]: item {item.Id} {name} has value(s)",
                    values.Count > 0, string.Join(", ", values.Select(v => v.Label)));

                foreach (var v in values)
                {
                    checkedAny = true;
                    var term = store.GetTerm(Guid.Parse(v.TermGuid));
                    tctx.Load(term, t => t.Id, t => t.Name);
                    await tctx.ExecuteQueryAsync();

                    var resolves = term.ServerObjectIsNull != true;
                    Program.Check($"tax-matrix [{tag}]: item {item.Id} {name} '{v.Label}' resolves in target store",
                        resolves, v.TermGuid);
                    if (resolves)
                        Program.Check($"tax-matrix [{tag}]: item {item.Id} {name} '{v.Label}' label matches term",
                            string.Equals(term.Name, v.Label, StringComparison.Ordinal), $"{term.Name} == {v.Label}");
                }
            }
        }
        Program.Check($"tax-matrix [{tag}]: taxonomy values were actually checked", checkedAny, "");
        if (isLibrary)
            Program.Check($"tax-matrix [{tag}]: the tagged FOLDER came across", sawFolder, TaggedFolder);
    }

    private static async Task<List<(string Label, Guid Id)>> ReadTermsAsync(ClientContext ctx, TermStore store, Guid termSetId)
    {
        var set = store.GetTermSet(termSetId);
        var terms = set.GetAllTerms();
        ctx.Load(terms, ts => ts.Include(t => t.Id, t => t.Name, t => t.PathOfTerm));
        await ctx.ExecuteQueryAsync();
        // Root terms only: enough to tag with, and free of path ambiguity.
        return terms.AsEnumerable()
            .Where(t => !t.PathOfTerm.Contains(';'))
            .Select(t => (t.Name, t.Id))
            .ToList();
    }

    /// <summary>Recreates the source list + library, each with all four taxonomy columns, and tags the content.</summary>
    private static async Task ProvisionSourceAsync(SpConnection source, Guid siteTermSetId, Guid globalTermSetId,
        List<(string Label, Guid Id)> siteTerms, List<(string Label, Guid Id)> globalTerms)
    {
        using var ctx = source.CreateContext();
        var session = TaxonomySession.GetTaxonomySession(ctx);
        var store = session.GetDefaultSiteCollectionTermStore();
        ctx.Load(store, s => s.Id);
        await ctx.ExecuteQueryAsync();

        await TestAssets.DeleteIfExistsAsync(ctx, SrcList);
        await TestAssets.DeleteIfExistsAsync(ctx, SrcLib);

        var list = ctx.Web.Lists.Add(new ListCreationInformation
        {
            Title = SrcList, TemplateType = (int)ListTemplateType.GenericList, Url = $"Lists/{SrcList}",
        });
        var lib = ctx.Web.Lists.Add(new ListCreationInformation
        {
            Title = SrcLib, TemplateType = (int)ListTemplateType.DocumentLibrary, Url = SrcLib,
        });
        ctx.Load(list, l => l.Id);
        ctx.Load(lib, l => l.Id, l => l.RootFolder.ServerRelativeUrl);
        await ctx.ExecuteQueryAsync();

        foreach (var target in new[] { list, lib })
        {
            foreach (var (name, siteScoped, multi) in Columns)
            {
                var type = multi ? "TaxonomyFieldTypeMulti" : "TaxonomyFieldType";
                var xml = $"<Field Type='{type}' DisplayName='{name}' Name='{name}' StaticName='{name}' "
                          + $"{(multi ? "Mult='TRUE' " : "")}/>";
                var created = target.Fields.AddFieldAsXml(xml, true, AddFieldOptions.AddFieldInternalNameHint);
                var tax = ctx.CastTo<TaxonomyField>(created);
                tax.SspId = store.Id;
                tax.TermSetId = siteScoped ? siteTermSetId : globalTermSetId;
                tax.AllowMultipleValues = multi;
                tax.Update();
                await ctx.ExecuteQueryAsync();
            }
        }

        // Multi-value taxonomy fields must be loaded ONCE up front: SetFieldValueByValueCollection needs
        // the field object, and an ExecuteQuery in the middle of tagging flushes the item's pending
        // values and loses them. (ItemCopier.PrimeTargetTaxonomyFieldsAsync exists for this same reason.)
        var listFields = await PrimeMultiFieldsAsync(ctx, list);
        var libFields = await PrimeMultiFieldsAsync(ctx, lib);

        // Two tagged list items.
        for (var i = 1; i <= 2; i++)
        {
            var item = list.AddItem(new ListItemCreationInformation());
            item["Title"] = $"matrix-item-{i}";
            Tag(item, listFields, siteTerms, globalTerms);
            item.Update();
            await ctx.ExecuteQueryAsync();
        }

        // Two tagged files at the library root.
        for (var i = 1; i <= 2; i++)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes($"taxonomy matrix test file {i}");
            var file = lib.RootFolder.Files.Add(new FileCreationInformation
            {
                Url = $"matrix-file-{i}.txt", Content = bytes, Overwrite = true,
            });
            ctx.Load(file, f => f.ListItemAllFields);
            await ctx.ExecuteQueryAsync();

            var item = file.ListItemAllFields;
            Tag(item, libFields, siteTerms, globalTerms);
            item.Update();
            await ctx.ExecuteQueryAsync();
        }

        // A TAGGED FOLDER, plus a file inside it. Folders carry columns too, and the Migration API
        // package has to write their taxonomy the same way it writes a file's.
        var folder = lib.RootFolder.Folders.Add(TaggedFolder);
        ctx.Load(folder, f => f.ListItemAllFields);
        await ctx.ExecuteQueryAsync();
        var folderItem = folder.ListItemAllFields;
        Tag(folderItem, libFields, siteTerms, globalTerms);
        folderItem.Update();
        await ctx.ExecuteQueryAsync();

        var nested = folder.Files.Add(new FileCreationInformation
        {
            Url = "nested-file.txt",
            Content = System.Text.Encoding.UTF8.GetBytes("taxonomy matrix nested file"),
            Overwrite = true,
        });
        ctx.Load(nested, f => f.ListItemAllFields);
        await ctx.ExecuteQueryAsync();
        var nestedItem = nested.ListItemAllFields;
        Tag(nestedItem, libFields, siteTerms, globalTerms);
        nestedItem.Update();
        await ctx.ExecuteQueryAsync();

        Console.WriteLine($"  provisioned source '{SrcList}' (2 items) and '{SrcLib}' "
                          + $"(2 files + tagged folder '{TaggedFolder}' + 1 nested file), 4 taxonomy columns each");
    }

    /// <summary>Loads the multi-value taxonomy field objects for a list, once.</summary>
    private static async Task<Dictionary<string, TaxonomyField>> PrimeMultiFieldsAsync(ClientContext ctx, List list)
    {
        var fields = new Dictionary<string, TaxonomyField>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, _, multi) in Columns.Where(c => c.Multi))
        {
            var f = ctx.CastTo<TaxonomyField>(list.Fields.GetByInternalNameOrTitle(name));
            ctx.Load(f, x => x.InternalName);
            fields[name] = f;
        }
        await ctx.ExecuteQueryAsync();
        return fields;
    }

    /// <summary>Sets all four taxonomy columns on one item: single takes term[0], multi takes term[0]+term[1].</summary>
    private static void Tag(ListItem item, Dictionary<string, TaxonomyField> multiFields,
        List<(string Label, Guid Id)> siteTerms, List<(string Label, Guid Id)> globalTerms)
    {
        foreach (var (name, siteScoped, multi) in Columns)
        {
            var terms = siteScoped ? siteTerms : globalTerms;
            if (!multi)
            {
                item[name] = new TaxonomyFieldValue
                {
                    WssId = -1, Label = terms[0].Label, TermGuid = terms[0].Id.ToString(),
                };
                continue;
            }

            var field = multiFields[name];
            var pairs = string.Join(";", terms.Take(2).Select(t => $"{t.Label}|{t.Id}"));
            var coll = new TaxonomyFieldValueCollection(field.Context, null, field);
            coll.PopulateFromLabelGuidPairs(pairs);
            field.SetFieldValueByValueCollection(item, coll);
        }
    }
}

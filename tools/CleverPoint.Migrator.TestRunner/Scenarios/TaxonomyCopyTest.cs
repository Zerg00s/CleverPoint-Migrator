using CleverPoint.Migrator.Core.Csom;
using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using Microsoft.SharePoint.Client;
using Microsoft.SharePoint.Client.Taxonomy;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Managed metadata copy (F9): provisions a source list with a taxonomy column bound to a term
/// set, tags items, copies the list, and verifies the target column is created + bound and the
/// items carry the same term (label + GUID). Same-tenant, so term GUIDs are identical.
/// </summary>
public static class TaxonomyCopyTest
{
    public static async Task RunAsync()
    {
        var site = Program.TestSite ?? await TestAssets.EnsureTestSiteAsync(Program.Source);
        Program.TestSite = site;

        // ---- Ensure the probe term set (MigTest Terms > MigTest Colors: Red/Green/Blue) ----
        Guid sspId, termSetId;
        var termByName = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        using (var ctx = site.CreateContext())
        {
            var session = TaxonomySession.GetTaxonomySession(ctx);
            var store = session.GetDefaultSiteCollectionTermStore();
            ctx.Load(store, s => s.Id, s => s.Groups.Include(g => g.Name, g => g.TermSets.Include(ts => ts.Name, ts => ts.Id)));
            await ctx.ExecuteWithRetryAsync();
            sspId = store.Id;

            var grp = store.Groups.FirstOrDefault(g => g.Name == "MigTest Terms") ?? store.CreateGroup("MigTest Terms", Guid.NewGuid());
            ctx.Load(grp, g => g.TermSets.Include(ts => ts.Name, ts => ts.Id));
            await ctx.ExecuteWithRetryAsync();
            var set = grp.TermSets.FirstOrDefault(ts => ts.Name == "MigTest Colors");
            if (set == null)
            {
                set = grp.CreateTermSet("MigTest Colors", Guid.NewGuid(), 1033);
                set.CreateTerm("Red", 1033, Guid.NewGuid());
                set.CreateTerm("Green", 1033, Guid.NewGuid());
                set.CreateTerm("Blue", 1033, Guid.NewGuid());
                store.CommitAll();
            }
            ctx.Load(set, s => s.Id, s => s.Terms.Include(t => t.Name, t => t.Id));
            await ctx.ExecuteWithRetryAsync();
            termSetId = set.Id;
            foreach (var t in set.Terms) termByName[t.Name] = t.Id;
        }
        Program.Check("taxcopy: term set ready (Red/Green/Blue)", termByName.Count >= 3, string.Join(", ", termByName.Keys));

        // ---- Provision source list with a taxonomy column + 3 tagged items ----
        const string srcTitle = "MigTest-Tax";
        const string tgtTitle = "MigTest-TaxCopy";
        const string field = "Color";           // single-value TaxonomyFieldType
        const string multiField = "Colors";     // multi-value TaxonomyFieldTypeMulti
        // Two terms per item, so the multi path (Label|Guid;#Label|Guid) is exercised.
        var multiPlan = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Red"] = new[] { "Red", "Green" },
            ["Green"] = new[] { "Green", "Blue" },
            ["Blue"] = new[] { "Blue", "Red" },
        };
        using (var ctx = site.CreateContext())
        {
            // Idempotent: the migtest site is under retention, so a prior run's list may linger
            // (DeleteIfExists returns false) - reuse + clear it instead of failing on Add.
            var srcDeleted = await TestAssets.DeleteIfExistsAsync(ctx, srcTitle);
            var tgtDeleted = await TestAssets.DeleteIfExistsAsync(ctx, tgtTitle);
            List list;
            TaxonomyField taxMulti;
            if (srcDeleted)
            {
                list = ctx.Web.Lists.Add(new ListCreationInformation
                {
                    Title = srcTitle, TemplateType = (int)ListTemplateType.GenericList, Url = "Lists/MigTestTax",
                });
                var created = list.Fields.AddFieldAsXml(
                    $"<Field Type='TaxonomyFieldType' DisplayName='{field}' Name='{field}' StaticName='{field}' />",
                    true, AddFieldOptions.AddFieldInternalNameHint);
                var tax = ctx.CastTo<TaxonomyField>(created);
                tax.SspId = sspId; tax.TermSetId = termSetId; tax.Update();

                var createdMulti = list.Fields.AddFieldAsXml(
                    $"<Field Type='TaxonomyFieldTypeMulti' DisplayName='{multiField}' Name='{multiField}' StaticName='{multiField}' Mult='TRUE' />",
                    true, AddFieldOptions.AddFieldInternalNameHint);
                taxMulti = ctx.CastTo<TaxonomyField>(createdMulti);
                taxMulti.SspId = sspId; taxMulti.TermSetId = termSetId; taxMulti.AllowMultipleValues = true; taxMulti.Update();
                await ctx.ExecuteWithRetryAsync();
            }
            else
            {
                // Retention kept the (cleared) list; schema (taxonomy columns) is intact.
                list = ctx.Web.Lists.GetByTitle(srcTitle);
                await TestAssets.ClearListAsync(ctx, list);
                taxMulti = ctx.CastTo<TaxonomyField>(list.Fields.GetByInternalNameOrTitle(multiField));
                ctx.Load(taxMulti, f => f.InternalName);
                await ctx.ExecuteWithRetryAsync();
            }
            // Target: if retention kept it, clear it so a re-copy doesn't duplicate items.
            if (!tgtDeleted)
            {
                var oldTgt = ctx.Web.Lists.GetByTitle(tgtTitle);
                await TestAssets.ClearListAsync(ctx, oldTgt);
            }

            foreach (var name in new[] { "Red", "Green", "Blue" })
            {
                var item = list.AddItem(new ListItemCreationInformation());
                item["Title"] = $"Item {name}";
                item[field] = new TaxonomyFieldValue { WssId = -1, Label = name, TermGuid = termByName[name].ToString() };
                // Multi: canonical CSOM populate-from-label-guid-pairs ("Label|Guid;Label|Guid").
                var pairs = string.Join(";", multiPlan[name].Select(t => $"{t}|{termByName[t]}"));
                var coll = new TaxonomyFieldValueCollection(ctx, null, taxMulti);
                coll.PopulateFromLabelGuidPairs(pairs);
                taxMulti.SetFieldValueByValueCollection(item, coll);
                item.Update();
            }
            await ctx.ExecuteWithRetryAsync();
        }
        Program.Check("taxcopy: source list + taxonomy columns provisioned (single + multi)", true, $"{srcTitle}.{field}/{multiField}");

        // ---- Copy ----
        var result = await CopyEngine.CopyListAsync(site, site, srcTitle,
            new CopyOptions { TargetListTitle = tgtTitle, TargetListUrl = "Lists/MigTestTaxCopy" });
        foreach (var r in result.Records.Where(r => r.Status == ItemCopyStatus.Failed))
            Console.WriteLine($"    FAILED {r.ItemType} {r.SourcePath}: {r.Message}");
        Program.Check("taxcopy: copy no failures", result.Failed == 0, result.Summary());
        var fieldCopied = result.Records.Any(r => r.ItemType == "Field" && r.SourcePath == field && r.Status == ItemCopyStatus.Copied);
        Program.Check("taxcopy: taxonomy column created + bound on target", fieldCopied,
            result.Records.FirstOrDefault(r => r.SourcePath == field)?.Message ?? "no field record");

        // ---- Verify target items carry the same term ----
        using (var ctx = site.CreateContext())
        {
            var tgt = ctx.Web.Lists.GetByTitle(tgtTitle);
            var items = tgt.GetItems(CamlQuery.CreateAllItemsQuery(50));
            ctx.Load(items, ii => ii.Include(i => i["Title"], i => i[field], i => i[multiField]));
            await ctx.ExecuteWithRetryAsync();

            var single = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var multi = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);
            foreach (var i in items)
            {
                var title = (string)i["Title"];
                if (i.FieldValues.GetValueOrDefault(field) is TaxonomyFieldValue v)
                    single[title] = $"{v.Label}|{v.TermGuid}";
                var guids = new HashSet<Guid>();
                if (i.FieldValues.GetValueOrDefault(multiField) is TaxonomyFieldValueCollection col)
                    foreach (var mv in col) if (Guid.TryParse(mv.TermGuid, out var g)) guids.Add(g);
                multi[title] = guids;
            }

            var okSingle = new[] { "Red", "Green", "Blue" }.All(n =>
                single.TryGetValue($"Item {n}", out var val)
                && val.StartsWith(n + "|", StringComparison.OrdinalIgnoreCase)
                && val.EndsWith(termByName[n].ToString(), StringComparison.OrdinalIgnoreCase));
            foreach (var kv in single) Console.WriteLine($"    single {kv.Key} -> {kv.Value}");
            Program.Check("taxcopy: single-value term copied (label + GUID)", okSingle, $"{single.Count} items");

            var okMulti = new[] { "Red", "Green", "Blue" }.All(n =>
            {
                var expected = multiPlan[n].Select(t => termByName[t]).ToHashSet();
                return multi.TryGetValue($"Item {n}", out var got) && got.SetEquals(expected);
            });
            foreach (var kv in multi) Console.WriteLine($"    multi {kv.Key} -> {kv.Value.Count} terms [{string.Join(", ", kv.Value)}]");
            Program.Check("taxcopy: multi-value terms copied (both terms per item)", okMulti,
                string.Join(" | ", multi.Select(kv => $"{kv.Key}={kv.Value.Count}")));
        }
    }
}

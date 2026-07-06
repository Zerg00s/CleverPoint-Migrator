using CleverPoint.Migrator.Core.Csom;
using Microsoft.SharePoint.Client;
using Microsoft.SharePoint.Client.Taxonomy;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Probe: can the app-only cert read (and ideally write) the tenant term store, and is there a
/// term set with terms we can bind an MMD column to for a managed-metadata copy test (F9)?
/// Read-only inventory + a write attempt, reported not asserted.
/// </summary>
public static class TaxonomyProbe
{
    public static async Task RunAsync()
    {
        using var ctx = Program.Source.CreateContext();
        var session = TaxonomySession.GetTaxonomySession(ctx);
        var store = session.GetDefaultSiteCollectionTermStore();
        ctx.Load(store, s => s.Name, s => s.Id, s => s.Groups.Include(g => g.Name, g => g.IsSystemGroup,
            g => g.TermSets.Include(ts => ts.Name, ts => ts.Id, ts => ts.Terms.Include(t => t.Name, t => t.Id))));
        try
        {
            await ctx.ExecuteWithRetryAsync();
        }
        catch (Exception ex)
        {
            Program.Check("taxprobe: term store readable by app-only cert", false, ex.Message);
            return;
        }

        Console.WriteLine($"  term store: '{store.Name}' ({store.Id})");
        var groups = store.Groups.Where(g => !g.IsSystemGroup).ToList();
        (string Group, string Set, Guid SetId, int Terms)? usable = null;
        foreach (var g in groups)
        {
            foreach (var ts in g.TermSets)
            {
                var termCount = ts.Terms.Count;
                Console.WriteLine($"    [{g.Name}] {ts.Name} ({ts.Id}) - {termCount} top terms");
                if (termCount > 0 && usable is null) usable = (g.Name, ts.Name, ts.Id, termCount);
            }
        }
        Program.Check("taxprobe: term store readable", true, $"'{store.Name}', {groups.Count} non-system groups");
        Program.Check("taxprobe: found a usable term set with terms", usable is not null,
            usable is { } u ? $"[{u.Group}] {u.Set} ({u.SetId}) with {u.Terms} terms" : "none - a term set must be created");

        // Write attempt: create a probe group/set/term so the MMD test can be self-contained.
        try
        {
            var probeGroupName = "MigTest Terms";
            var existing = store.Groups.FirstOrDefault(g => g.Name == probeGroupName);
            TermGroup grp;
            if (existing != null) grp = existing;
            else grp = store.CreateGroup(probeGroupName, Guid.NewGuid());
            ctx.Load(grp, g => g.Name, g => g.TermSets.Include(ts => ts.Name, ts => ts.Id));
            await ctx.ExecuteWithRetryAsync();

            var setName = "MigTest Colors";
            var set = grp.TermSets.FirstOrDefault(ts => ts.Name == setName);
            if (set == null)
            {
                set = grp.CreateTermSet(setName, Guid.NewGuid(), 1033);
                set.CreateTerm("Red", 1033, Guid.NewGuid());
                set.CreateTerm("Green", 1033, Guid.NewGuid());
                set.CreateTerm("Blue", 1033, Guid.NewGuid());
                store.CommitAll();
            }
            ctx.Load(set, s => s.Name, s => s.Id, s => s.Terms.Include(t => t.Name, t => t.Id));
            await ctx.ExecuteWithRetryAsync();
            Console.WriteLine($"    probe set '{set.Name}' ({set.Id}) terms: {string.Join(", ", set.Terms.Select(t => $"{t.Name}={t.Id}"))}");
            Program.Check("taxprobe: term store WRITABLE (created/loaded probe set)", set.Terms.Count >= 3,
                $"{set.Terms.Count} terms in '{set.Name}'");
        }
        catch (Exception ex)
        {
            Program.Check("taxprobe: term store writable by app-only cert", false, ex.Message);
        }
    }
}

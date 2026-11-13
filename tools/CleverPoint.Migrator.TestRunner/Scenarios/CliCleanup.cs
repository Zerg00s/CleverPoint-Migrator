using CleverPoint.Migrator.Core.Csom;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Removes the lists the command-line tests (tools/cli-tests/cli_tests.py) leave behind: everything
/// whose title starts with "CliT-" or "MigTest-Cli", on the test subsite and on the lab tenant's root
/// site. Nothing else is touched.
/// </summary>
public static class CliCleanup
{
    public static async Task RunAsync()
    {
        var site = Program.TestSite ?? await TestAssets.EnsureTestSiteAsync(Program.Source);
        Program.TestSite = site;
        foreach (var (name, connection) in new[] { ("test subsite", site), ("lab tenant root", Program.Target) })
        {
            using var ctx = connection.CreateContext();
            var lists = ctx.Web.Lists;
            ctx.Load(lists, ls => ls.Include(l => l.Title, l => l.Id));
            await ctx.ExecuteWithRetryAsync();
            var mine = lists.AsEnumerable()
                .Where(l => l.Title.StartsWith("CliT-", StringComparison.OrdinalIgnoreCase) || l.Title.StartsWith("MigTest-Cli", StringComparison.OrdinalIgnoreCase))
                .Select(l => (l.Title, l.Id)).ToList();
            var removed = 0;
            foreach (var (title, id) in mine)
            {
                try
                {
                    ctx.Web.Lists.GetById(id).DeleteObject();
                    await ctx.ExecuteWithRetryAsync();
                    removed++;
                }
                catch (ServerException ex)
                {
                    Console.WriteLine($"  could not remove '{title}': {ex.Message}");
                }
            }
            Program.Check($"cli-cleanup: {name}", removed == mine.Count, $"removed {removed} of {mine.Count} list(s)");
        }
    }
}

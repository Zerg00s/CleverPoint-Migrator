using CleverPoint.Migrator.Core.Csom;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Proves REST and CSOM both work app-only against source and target tenants.
/// </summary>
public static class AuthSmokeTest
{
    public static async Task RunAsync()
    {
        // REST on source
        using var sourceWeb = await Program.Source.Rest.GetJsonAsync($"{Program.Source.SiteUrl}/_api/web?$select=Title,Url");
        Program.Check("REST source web", true, sourceWeb.RootElement.GetProperty("Title").GetString());

        // REST on target (root site)
        using var targetWeb = await Program.Target.Rest.GetJsonAsync($"{Program.Target.SiteUrl}/_api/web?$select=Title,Url");
        Program.Check("REST target web", true, targetWeb.RootElement.GetProperty("Title").GetString());

        // CSOM on source
        using (var ctx = Program.Source.CreateContext())
        {
            ctx.Load(ctx.Web, w => w.Title, w => w.ServerRelativeUrl, w => w.Lists.Include(l => l.Title));
            await ctx.ExecuteWithRetryAsync();
            Program.Check("CSOM source web", ctx.Web.Title.Length > 0,
                $"'{ctx.Web.Title}', {ctx.Web.Lists.Count} lists");
        }

        // CSOM on target
        using (var ctx = Program.Target.CreateContext())
        {
            ctx.Load(ctx.Web, w => w.Title);
            await ctx.ExecuteWithRetryAsync();
            Program.Check("CSOM target web", true, $"'{ctx.Web.Title}'");
        }
    }
}

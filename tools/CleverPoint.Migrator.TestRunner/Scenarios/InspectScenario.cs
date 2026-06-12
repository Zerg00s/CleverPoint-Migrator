using CleverPoint.Migrator.Core.Csom;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>Debug helper: dumps items of a list on the test site.</summary>
public static class InspectScenario
{
    public static async Task RunAsync()
    {
        var site = Program.TestSite ?? await TestAssets.EnsureTestSiteAsync(Program.Source);
        using var ctx = site.CreateContext();
        var list = ctx.Web.Lists.GetByTitle(Environment.GetEnvironmentVariable("INSPECT_LIST") ?? "MigTest-Copy");
        var query = new CamlQuery { ViewXml = "<View Scope='RecursiveAll'><RowLimit>8</RowLimit></View>" };
        var items = list.GetItems(query);
        ctx.Load(items);
        await ctx.ExecuteQueryAsync();

        foreach (var item in items)
        {
            string Get(string f) => item.FieldValues.TryGetValue(f, out var v) && v != null
                ? v is FieldUserValue u ? $"{u.LookupValue}({u.Email})" : v.ToString() ?? "" : "(null)";
            Console.WriteLine($"  #{item.Id} FileRef={Get("FileRef")}");
            Console.WriteLine($"     Title={Get("Title")} TextCol={Get("TextCol")} Choice={Get("ChoiceCol")} Num={Get("NumberCol")}");
            Console.WriteLine($"     Created={Get("Created")} Modified={Get("Modified")} Author={Get("Author")} Editor={Get("Editor")}");
        }
        Program.Check("inspect dump", true, $"{items.Count} items shown");
    }
}

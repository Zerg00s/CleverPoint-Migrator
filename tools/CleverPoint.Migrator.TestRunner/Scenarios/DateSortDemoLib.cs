using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Provisions a PERSISTENT little library for manually eyeballing the Explorer "Modified"-sort fix.
/// The files are named with their day-of-month, and their day order is the REVERSE of chronological order,
/// so a correct (instant) sort and a broken (string/day) sort produce visibly different orderings.
/// Does not delete anything -- run once, then open it in the app.
/// </summary>
public static class DateSortDemoLib
{
    private const string Site = "https://gocleverpointcom.sharepoint.com/sites/DemoLargeSite";
    private const string Library = "_DateSortCheck";

    public static async Task RunAsync()
    {
        var source = Program.Source;   // gocleverpointcom/sites/DemoLargeSite
        using var ctx = source.CreateContext();

        await TestAssets.DeleteIfExistsAsync(ctx, Library);
        var lib = ctx.Web.Lists.Add(new ListCreationInformation
        {
            Title = Library, TemplateType = (int)ListTemplateType.DocumentLibrary, Url = Library,
        });
        ctx.Load(lib, l => l.RootFolder.ServerRelativeUrl);
        await ctx.ExecuteQueryAsync();

        // Chronological order is A->E (oldest to newest). Day-of-month order is the OPPOSITE (28..02),
        // so a broken by-day sort inverts the list -- easy to see.
        var files = new (string Name, DateTime When)[]
        {
            ("A - oldest (2019, day 28).txt", new DateTime(2019, 1, 28, 9, 0, 0, DateTimeKind.Utc)),
            ("B - 2021 (day 17).txt",         new DateTime(2021, 6, 17, 9, 0, 0, DateTimeKind.Utc)),
            ("C - 2023 (day 11).txt",         new DateTime(2023, 3, 11, 9, 0, 0, DateTimeKind.Utc)),
            ("D - 2025 (day 05).txt",         new DateTime(2025, 9, 5, 9, 0, 0, DateTimeKind.Utc)),
            ("E - newest (2026, day 02).txt", new DateTime(2026, 12, 2, 9, 0, 0, DateTimeKind.Utc)),
        };
        foreach (var (name, when) in files)
        {
            var file = lib.RootFolder.Files.Add(new FileCreationInformation
            {
                Url = name, Content = System.Text.Encoding.UTF8.GetBytes(name), Overwrite = true,
            });
            ctx.Load(file, f => f.ListItemAllFields);
            await ctx.ExecuteQueryAsync();
            var item = file.ListItemAllFields;
            item["Created"] = when;
            item["Modified"] = when;
            item.UpdateOverwriteVersion();
            await ctx.ExecuteQueryAsync();
        }

        Console.WriteLine();
        Console.WriteLine($"  Ready. In the app, open SOURCE = {Site}");
        Console.WriteLine($"  and browse into the '{Library}' library, then click the 'Modified' header.");
        Console.WriteLine("  Correct (fixed): A, B, C, D, E ascending  /  E, D, C, B, A descending.");
        Console.WriteLine("  Broken (old bug): order would follow the day number (28,17,11,05,02), not the year.");
        Program.Check("date-demo: library provisioned with 5 back-dated files", true, $"{Site}/{Library}");
    }
}

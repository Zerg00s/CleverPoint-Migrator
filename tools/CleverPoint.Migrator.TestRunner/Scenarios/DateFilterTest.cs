using CleverPoint.Migrator.Core.Csom;
using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Verifies the date filter actually filters — for both a generic list
/// (ItemCopier) and a document library (FileCopier), on Modified and Created.
/// </summary>
public static class DateFilterTest
{
    public static async Task RunAsync()
    {
        var site = Program.TestSite ?? await TestAssets.EnsureTestSiteAsync(Program.Source);
        Program.TestSite = site;

        var future = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var past = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // ---- Generic list ----
        await CopyAndCount(site, TestAssets.SourceListTitle, "MigTest-DF-ListAll",
            new CopyOptions { TargetListTitle = "MigTest-DF-ListAll", TargetListUrl = "Lists/MigTestDFListAll" },
            "list, no filter", expectNonZero: true);

        await CopyAndCount(site, TestAssets.SourceListTitle, "MigTest-DF-ListNone",
            new CopyOptions { TargetListTitle = "MigTest-DF-ListNone", TargetListUrl = "Lists/MigTestDFListNone",
                ModifiedSinceUtc = future, DateField = DateFilterField.Modified },
            "list, modified-since 2099 (expect 0)", expectZero: true);

        await CopyAndCount(site, TestAssets.SourceListTitle, "MigTest-DF-ListCreated",
            new CopyOptions { TargetListTitle = "MigTest-DF-ListCreated", TargetListUrl = "Lists/MigTestDFListCreated",
                ModifiedBeforeUtc = past, DateField = DateFilterField.Created },
            "list, created-before 2000 (expect 0)", expectZero: true);

        // ---- Document library ----
        await CopyAndCount(site, TestAssets.SourceLibTitle, "MigTest-DF-LibAll",
            new CopyOptions { TargetListTitle = "MigTest-DF-LibAll", TargetListUrl = "MigTestDFLibAll" },
            "library, no filter", expectNonZero: true);

        await CopyAndCount(site, TestAssets.SourceLibTitle, "MigTest-DF-LibNone",
            new CopyOptions { TargetListTitle = "MigTest-DF-LibNone", TargetListUrl = "MigTestDFLibNone",
                ModifiedSinceUtc = future, DateField = DateFilterField.Modified },
            "library, modified-since 2099 (expect 0)", expectZero: true);
    }

    private static async Task CopyAndCount(SpConnection site, string sourceTitle, string targetTitle,
        CopyOptions options, string label, bool expectZero = false, bool expectNonZero = false)
    {
        using (var ctx = site.CreateContext()) await TestAssets.DeleteIfExistsAsync(ctx, targetTitle);
        var result = await CopyEngine.CopyListAsync(site, site, sourceTitle, options);
        var items = result.Records.Count(r => (r.ItemType == "Item" || r.ItemType == "File") && r.Status == ItemCopyStatus.Copied);
        Console.WriteLine($"  {label}: {items} items copied ({result.Summary()})");
        if (expectZero) Program.Check($"date-filter: {label}", items == 0, $"{items} items");
        if (expectNonZero) Program.Check($"date-filter: {label}", items > 0, $"{items} items");
    }
}

using CleverPoint.Migrator.Core.Auth;
using CleverPoint.Migrator.Core.Csom;
using CleverPoint.Migrator.Core.Model;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// The Explorer "Modified" column sorted by the DISPLAY STRING, so a locale-formatted date sorted by its
/// leading number -- on a dd/MM locale, by day-of-month (the user's report). The fix keeps a real instant
/// (ModifiedUtc) as the sort key and only formats for display.
///
/// Two halves:
///   1. Deterministic: DateText.TryParseIso yields instants that sort chronologically, where the SAME dates
///      as dd/MM (and M/d) strings sort WRONG -- proving both the bug and the fix without a tenant.
///   2. Live contract: RenderListDataAsStream with DatesInUtc=true (what SiteBrowser now sends) returns
///      ISO-8601 that parses, and sorting by it matches SharePoint's own $orderby=Modified. Run against a
///      throwaway library with back-dated files whose day-of-month order != chronological order.
/// </summary>
public static class DateSortTest
{
    private const string TargetSite = "https://cleverpointlab.sharepoint.com/sites/intranet";
    private const string Library = "DateSort-Lib";

    public static async Task RunAsync()
    {
        DeterministicHalf();
        await LiveHalfAsync();
    }

    private static void DeterministicHalf()
    {
        // Chronological order: Jan 25 -> Feb 10 -> Mar 05 -> Nov 03 2025 (oldest last on purpose).
        var rows = new[]
        {
            (Iso: "2026-01-25T09:00:00Z", DayMonth: "25/01/2026 09:00", MonthDay: "1/25/2026 9:00 AM"),
            (Iso: "2026-02-10T09:00:00Z", DayMonth: "10/02/2026 09:00", MonthDay: "2/10/2026 9:00 AM"),
            (Iso: "2026-03-05T09:00:00Z", DayMonth: "05/03/2026 09:00", MonthDay: "3/5/2026 9:00 AM"),
            (Iso: "2025-11-03T09:00:00Z", DayMonth: "03/11/2025 09:00", MonthDay: "11/3/2025 9:00 AM"),
        };

        var chronological = rows.OrderBy(r => DateText.TryParseIso(r.Iso)).Select(r => r.Iso).ToList();
        var expected = new[] { "2025-11-03T09:00:00Z", "2026-01-25T09:00:00Z", "2026-02-10T09:00:00Z", "2026-03-05T09:00:00Z" };
        Program.Check("date-sort: instants sort chronologically",
            chronological.SequenceEqual(expected), string.Join(" | ", chronological));

        // The bug: sorting the dd/MM display strings orders by leading day (03, 05, 10, 25) -- NOT chronological.
        var byDayMonthString = rows.OrderBy(r => r.DayMonth).Select(r => r.Iso).ToList();
        Program.Check("date-sort: dd/MM STRING sort is NOT chronological (this was the bug)",
            !byDayMonthString.SequenceEqual(expected), string.Join(" | ", byDayMonthString));
        Program.Check("date-sort: dd/MM STRING sort actually orders by day-of-month",
            byDayMonthString.SequenceEqual(new[]
            {
                "2025-11-03T09:00:00Z", "2026-03-05T09:00:00Z", "2026-02-10T09:00:00Z", "2026-01-25T09:00:00Z",
            }), string.Join(" | ", byDayMonthString));

        // The US M/d display string is ALSO wrong (orders by leading month as text: "1","11","2","3").
        var byMonthDayString = rows.OrderBy(r => r.MonthDay).Select(r => r.Iso).ToList();
        Program.Check("date-sort: M/d STRING sort is NOT chronological either",
            !byMonthDayString.SequenceEqual(expected), string.Join(" | ", byMonthDayString));

        // Unparseable / blank -> null, sorts first, never throws.
        Program.Check("date-sort: blank date parses to null", DateText.TryParseIso("") == null, "");
        Program.Check("date-sort: junk date parses to null", DateText.TryParseIso("not a date") == null, "");

        // Round-trip: a UTC instant formats to local and still represents the same moment.
        var dt = DateText.TryParseIso("2026-03-05T14:30:00Z");
        Program.Check("date-sort: ISO Z parses to the correct UTC instant",
            dt.HasValue && dt.Value.ToUniversalTime() == new DateTime(2026, 3, 5, 14, 30, 0, DateTimeKind.Utc),
            dt?.ToString("o") ?? "<null>");
    }

    private static async Task LiveHalfAsync()
    {
        var target = new SpConnection(TargetSite, new CertTokenProvider(Program.TargetCreds));
        using var ctx = target.CreateContext();

        await TestAssets.DeleteIfExistsAsync(ctx, Library);
        var lib = ctx.Web.Lists.Add(new ListCreationInformation
        {
            Title = Library, TemplateType = (int)ListTemplateType.DocumentLibrary, Url = Library,
        });
        ctx.Load(lib, l => l.Id, l => l.RootFolder.ServerRelativeUrl);
        await ctx.ExecuteQueryAsync();
        var listUrl = lib.RootFolder.ServerRelativeUrl;

        // Files whose day-of-month order (25, 10, 05, 03) is the REVERSE of chronological order.
        var files = new (string Name, DateTime ModifiedUtc)[]
        {
            ("older-but-day25.txt", new DateTime(2026, 1, 25, 9, 0, 0, DateTimeKind.Utc)),
            ("mid-day10.txt",       new DateTime(2026, 2, 10, 9, 0, 0, DateTimeKind.Utc)),
            ("newer-day05.txt",     new DateTime(2026, 3, 5, 9, 0, 0, DateTimeKind.Utc)),
            ("oldest-day03.txt",    new DateTime(2025, 11, 3, 9, 0, 0, DateTimeKind.Utc)),
        };
        foreach (var (name, modified) in files)
        {
            var file = lib.RootFolder.Files.Add(new FileCreationInformation
            {
                Url = name, Content = System.Text.Encoding.UTF8.GetBytes(name), Overwrite = true,
            });
            ctx.Load(file, f => f.ListItemAllFields);
            await ctx.ExecuteQueryAsync();
            var item = file.ListItemAllFields;
            item["Created"] = modified;
            item["Modified"] = modified;
            item.UpdateOverwriteVersion();
            await ctx.ExecuteQueryAsync();
        }

        // The exact request SiteBrowser now issues: RenderListDataAsStream with DatesInUtc=true.
        var isoRows = await ReadModifiedAsync(target, listUrl, datesInUtc: true);
        Program.Check("date-sort: live library returned all rows", isoRows.Count == files.Length, $"{isoRows.Count} row(s)");

        // Every Modified value must parse as ISO to an instant (this is the wire contract the fix relies on).
        var parsed = isoRows.Select(r => (r.Name, Utc: DateText.TryParseIso(r.Modified))).ToList();
        Program.Check("date-sort: every live Modified parses as ISO instant",
            parsed.All(p => p.Utc != null), string.Join(", ", parsed.Where(p => p.Utc == null).Select(p => $"{p.Name}='{FindRaw(isoRows, p.Name)}'")));

        // Sorting by the parsed instant matches the chronological expectation.
        var chronological = parsed.Where(p => p.Utc != null).OrderBy(p => p.Utc).Select(p => p.Name).ToList();
        var expected = new[] { "oldest-day03.txt", "older-but-day25.txt", "mid-day10.txt", "newer-day05.txt" };
        Program.Check("date-sort: instant sort of live data is chronological",
            chronological.SequenceEqual(expected), string.Join(" -> ", chronological));

        // And it agrees with SharePoint's authoritative ordering.
        var serverOrder = await ServerOrderAsync(ctx, lib);
        Program.Check("date-sort: instant sort matches SharePoint's own $orderby=Modified",
            chronological.SequenceEqual(serverOrder), $"ours=[{string.Join(",", chronological)}] server=[{string.Join(",", serverOrder)}]");

        // Contrast: WITHOUT DatesInUtc, the values are friendly strings that do NOT sort chronologically.
        var friendlyRows = await ReadModifiedAsync(target, listUrl, datesInUtc: false);
        var byString = friendlyRows.OrderBy(r => r.Modified, StringComparer.Ordinal).Select(r => r.Name).ToList();
        Program.Check("date-sort: friendly-STRING sort of live data is NOT chronological (the old behavior)",
            !byString.SequenceEqual(expected), $"string order=[{string.Join(",", byString)}]");

        await TestAssets.DeleteIfExistsAsync(ctx, Library);
    }

    private static string FindRaw(List<(string Name, string Modified)> rows, string name) =>
        rows.FirstOrDefault(r => r.Name == name).Modified ?? "";

    /// <summary>Replicates SiteBrowser's exact RenderListDataAsStream REST read (name + raw Modified value).</summary>
    private static async Task<List<(string Name, string Modified)>> ReadModifiedAsync(SpConnection conn, string listUrl, bool datesInUtc)
    {
        var body = new
        {
            parameters = new
            {
                RenderOptions = 2,
                DatesInUtc = datesInUtc,
                ViewXml = "<View><Query></Query><ViewFields><FieldRef Name='FileLeafRef'/><FieldRef Name='FSObjType'/>"
                          + "<FieldRef Name='Modified'/></ViewFields><RowLimit Paged='TRUE'>500</RowLimit></View>",
            },
        };
        var response = await conn.Rest.PostAsync(
            $"{conn.SiteUrl}/_api/web/GetList(@a1)/RenderListDataAsStream?@a1='{listUrl}'", body);
        using var doc = System.Text.Json.JsonDocument.Parse(response);

        var rows = new List<(string, string)>();
        foreach (var row in doc.RootElement.GetProperty("Row").EnumerateArray())
        {
            if (row.TryGetProperty("FSObjType", out var t) && t.GetString() == "1") continue;
            var name = row.GetProperty("FileLeafRef").GetString() ?? "";
            var modified = row.TryGetProperty("Modified", out var m) ? m.GetString() ?? "" : "";
            rows.Add((name, modified));
        }
        return rows;
    }

    private static async Task<List<string>> ServerOrderAsync(ClientContext ctx, List lib)
    {
        var items = lib.GetItems(new CamlQuery
        {
            ViewXml = "<View Scope='RecursiveAll'><Query><OrderBy><FieldRef Name='Modified' Ascending='TRUE'/></OrderBy>"
                      + "<Where><Eq><FieldRef Name='FSObjType'/><Value Type='Integer'>0</Value></Eq></Where></Query>"
                      + "<ViewFields><FieldRef Name='FileLeafRef'/></ViewFields></View>",
        });
        ctx.Load(items, c => c.Include(i => i["FileLeafRef"]));
        await ctx.ExecuteQueryAsync();
        return items.AsEnumerable().Select(i => i["FileLeafRef"]?.ToString() ?? "").ToList();
    }
}

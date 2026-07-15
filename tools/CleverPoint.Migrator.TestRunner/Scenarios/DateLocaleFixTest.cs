using CleverPoint.Migrator.Core.Csom;
using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Verifies the fix for the ValidateUpdateListItem locale bug across three non-US locales. The document
/// form-update path (ApplyDocumentMetadataFormUpdateAsync) is what OneDrive/browser-sign-in migrations use
/// to stamp Created/Modified, and it writes a date STRING that the target web parses per its own locale.
/// The old code sent a fixed US "M/d/yyyy", so a French/UK/German web swapped day and month (or errored).
///
/// For each locale this:
///   1. picks tricky dates -- one where day &gt; 12 (US format is an INVALID month there) and one swappable
///      (day and month both &lt;= 12, so a swap silently produces a real-but-wrong date);
///   2. runs the real engine method and asserts the dates round-trip (day AND month correct);
///   3. as a teeth-check, writes the SAME dates the OLD way (US InvariantCulture) and asserts THAT is
///      wrong or rejected -- proving the locale genuinely matters and the fix is what saves it.
/// </summary>
public static class DateLocaleFixTest
{
    private static readonly (string Name, int Lcid, string Leaf)[] Locales =
    {
        ("fr-FR", 1036, "datelocale-fr"),
        ("en-GB", 2057, "datelocale-gb"),
        ("de-DE", 1031, "datelocale-de"),
    };

    // Day 25 -> US "3/25" is an invalid month on dd/MM. 5 May -> US "5/5" no help; use 4 Nov (day>12-free swap):
    // Created 25 Mar 2026 09:00 (day>12), Modified 04 Nov 2026 14:30 (swappable: 4/11 vs 11/4).
    private static readonly DateTime CreatedUtc = new(2026, 3, 25, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ModifiedUtc = new(2026, 11, 4, 14, 30, 0, DateTimeKind.Utc);

    public static async Task RunAsync()
    {
        var host = Program.Source;
        foreach (var (name, lcid, leaf) in Locales)
        {
            SpConnection sub;
            try
            {
                (sub, _) = await FrenchWeb.EnsureAsync(host, lcid, leaf);
            }
            catch (Exception ex)
            {
                Program.Check($"date-locale-fix [{name}]: subsite created", false, ex.Message);
                continue;
            }

            try
            {
                await RunLocaleAsync(name, lcid, sub);
            }
            catch (Exception ex)
            {
                Program.Check($"date-locale-fix [{name}]: scenario ran", false, ex.Message);
            }
            finally
            {
                await FrenchWeb.DeleteAsync(host, leaf);
            }
        }
    }

    private static async Task RunLocaleAsync(string name, int lcid, SpConnection sub)
    {
        using var ctx = sub.CreateContext();
        ctx.Load(ctx.Web.RegionalSettings, r => r.LocaleId);
        await ctx.ExecuteQueryAsync();
        Program.Check($"date-locale-fix [{name}]: web is locale {lcid}",
            (int)ctx.Web.RegionalSettings.LocaleId == lcid, $"{ctx.Web.RegionalSettings.LocaleId}");

        var lib = ctx.Web.Lists.Add(new ListCreationInformation
        {
            Title = "DL", TemplateType = (int)ListTemplateType.DocumentLibrary, Url = "DL",
        });
        ctx.Load(lib, l => l.RootFolder.ServerRelativeUrl);
        await ctx.ExecuteQueryAsync();

        // A SOURCE file whose Created/Modified are the tricky dates (set via the locale-independent
        // DateTime path, so the source itself is correct regardless of locale).
        var sourceItem = await AddFileWithDatesAsync(ctx, lib, "source.txt", CreatedUtc, ModifiedUtc);

        // A TARGET file we stamp via the real engine method under test.
        var targetItem = await AddFileWithDatesAsync(ctx, lib, "target.txt", null, null);

        var users = new UserResolver(ctx, ctx, null, null);
        await users.PrimeSourceUsersAsync();
        var copier = new ItemCopier(ctx, ctx, users);

        // THE FIX under test: form-update document metadata, dates formatted in the web's culture.
        await copier.ApplyDocumentMetadataFormUpdateAsync(sourceItem, targetItem, new CopyResult(), "target.txt");

        var (tc, tm) = await ReadDatesAsync(ctx, targetItem);
        Program.Check($"date-locale-fix [{name}]: Created day+month correct (25 Mar)",
            tc.HasValue && tc.Value.Month == 3 && tc.Value.Day == 25, $"{tc:yyyy-MM-dd HH:mm}");
        Program.Check($"date-locale-fix [{name}]: Modified day+month correct (04 Nov)",
            tm.HasValue && tm.Value.Month == 11 && tm.Value.Day == 4, $"{tm:yyyy-MM-dd HH:mm}");

        // Teeth-check: the OLD US-format write on THIS web is wrong (swap) or rejected (invalid month).
        var oldItem = await AddFileWithDatesAsync(ctx, lib, "old.txt", null, null);
        var timeZone = ctx.Web.RegionalSettings.TimeZone;
        var createdLocal = timeZone.UTCToLocalTime(CreatedUtc);
        await ctx.ExecuteQueryAsync();
        var usString = createdLocal.Value.ToString("M/d/yyyy h:mm tt", System.Globalization.CultureInfo.InvariantCulture);
        var form = new List<ListItemFormUpdateValue> { new() { FieldName = "Created", FieldValue = usString } };
        var validation = oldItem.ValidateUpdateListItem(form, true, "", false, false, "");
        await ctx.ExecuteQueryAsync();
        var errored = validation.Any(v => v.HasException);
        var (oldCreated, _) = await ReadDatesAsync(ctx, oldItem);
        var oldWrong = errored || !(oldCreated.HasValue && oldCreated.Value.Month == 3 && oldCreated.Value.Day == 25);
        Program.Check($"date-locale-fix [{name}]: OLD US format is wrong/rejected here (proves the bug + teeth)",
            oldWrong, errored ? "rejected (invalid month)" : $"stored {oldCreated:yyyy-MM-dd} (swapped)");
    }

    private static async Task<ListItem> AddFileWithDatesAsync(ClientContext ctx, List lib, string url, DateTime? createdUtc, DateTime? modifiedUtc)
    {
        var file = lib.RootFolder.Files.Add(new FileCreationInformation
        {
            Url = url, Content = System.Text.Encoding.UTF8.GetBytes(url), Overwrite = true,
        });
        ctx.Load(file, f => f.ListItemAllFields);
        await ctx.ExecuteQueryAsync();
        var item = file.ListItemAllFields;
        if (createdUtc.HasValue || modifiedUtc.HasValue)
        {
            if (createdUtc.HasValue) item["Created"] = createdUtc.Value;
            if (modifiedUtc.HasValue) item["Modified"] = modifiedUtc.Value;
            item.UpdateOverwriteVersion();
            await ctx.ExecuteQueryAsync();
        }
        ctx.Load(item, i => i["Created"], i => i["Modified"]);
        await ctx.ExecuteQueryAsync();
        return item;
    }

    private static async Task<(DateTime? Created, DateTime? Modified)> ReadDatesAsync(ClientContext ctx, ListItem item)
    {
        ctx.Load(item, i => i["Created"], i => i["Modified"]);
        await ctx.ExecuteQueryAsync();
        // SP returns Created/Modified as web-local wall-clock (Unspecified). We only assert day/month,
        // which the timezone cannot shift for a 09:00 / 14:30 value, so no conversion is needed.
        return (item["Created"] as DateTime?, item["Modified"] as DateTime?);
    }
}

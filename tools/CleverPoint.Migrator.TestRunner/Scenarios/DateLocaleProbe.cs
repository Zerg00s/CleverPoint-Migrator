using CleverPoint.Migrator.Core.Auth;
using CleverPoint.Migrator.Core.Csom;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// PROBE (not an assertion of correctness): what date-string format does ValidateUpdateListItem accept on
/// a FRENCH-locale (dd/MM/yyyy) web? The engine currently sends "M/d/yyyy h:mm tt" (US), and
/// ValidateUpdateListItem parses per the WEB's locale -- so on a French tenant a date like March 5 ("3/5")
/// is read as 3 May, and March 25 ("3/25") is an invalid month. This creates a throwaway French subsite,
/// writes Created via several candidate formats, reads each back, and prints which one round-trips.
///
/// Read the output to choose the fix; the assertion of the fix lives in date-locale-fix.
/// </summary>
public static class DateLocaleProbe
{
    private const int FrenchLcid = 1036;

    public static async Task RunAsync()
    {
        // Subsite creation is disabled on the target tenant, so build the French web on the SOURCE tenant.
        // The bug is about how a web parses date STRINGS per its own locale -- tenant is irrelevant.
        var host = Program.Source;
        var (sub, leaf) = await FrenchWeb.EnsureAsync(host, FrenchLcid);
        try
        {
            using var ctx = sub.CreateContext();
            ctx.Load(ctx.Web, w => w.Title);
            ctx.Load(ctx.Web.RegionalSettings, r => r.LocaleId);
            await ctx.ExecuteQueryAsync();
            var localeId = ctx.Web.RegionalSettings.LocaleId;
            Console.WriteLine($"  subsite '{leaf}' locale = {localeId}");
            Program.Check("date-locale-probe: subsite is French locale (1036)", localeId == FrenchLcid, $"{localeId}");

            var list = ctx.Web.Lists.Add(new ListCreationInformation
            {
                Title = "ProbeList", TemplateType = (int)ListTemplateType.GenericList, Url = "Lists/ProbeList",
            });
            await ctx.ExecuteQueryAsync();

            // Intended value: 5 March 2026, 09:00. On dd/MM this is unambiguous only if we DON'T swap.
            var intended = new DateTime(2026, 3, 5, 9, 0, 0);
            var candidates = new (string Label, string Value)[]
            {
                ("US M/d/yyyy h:mm tt", intended.ToString("M/d/yyyy h:mm tt", System.Globalization.CultureInfo.InvariantCulture)),
                ("ISO yyyy-MM-ddTHH:mm:ss", intended.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)),
                ("ISO-Z yyyy-MM-ddTHH:mm:ssZ", DateTime.SpecifyKind(intended, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture)),
                ("fr-FR dd/MM/yyyy HH:mm", intended.ToString("dd/MM/yyyy HH:mm", System.Globalization.CultureInfo.GetCultureInfo("fr-FR"))),
            };

            foreach (var (label, value) in candidates)
            {
                var item = list.AddItem(new ListItemCreationInformation());
                item["Title"] = label;
                item.Update();
                await ctx.ExecuteQueryAsync();

                var form = new List<ListItemFormUpdateValue>
                {
                    new() { FieldName = "Created", FieldValue = value },
                };
                var validation = item.ValidateUpdateListItem(form, false, "", false, false, "");
                await ctx.ExecuteQueryAsync();
                var err = validation.FirstOrDefault(v => v.HasException);

                ctx.Load(item, i => i["Created"]);
                await ctx.ExecuteQueryAsync();
                var readBack = item["Created"] is DateTime d ? d : (DateTime?)null;
                var roundTrips = readBack.HasValue && readBack.Value.Month == 3 && readBack.Value.Day == 5;

                Console.WriteLine($"  [{label,-28}] sent='{value}' -> "
                                  + (err != null ? $"ERROR '{err.ErrorMessage}'"
                                     : $"stored={readBack:yyyy-MM-dd HH:mm} {(roundTrips ? "OK (5 March)" : "WRONG (day/month swapped or off)")}"));
            }
        }
        finally
        {
            await FrenchWeb.DeleteAsync(host, leaf);
        }
    }
}

/// <summary>Creates/removes a throwaway subsite forced to a given regional locale, for date-locale tests.</summary>
public static class FrenchWeb
{
    public static async Task<(SpConnection Sub, string Leaf)> EnsureAsync(SpConnection parent, int lcid, string leaf = "datelocale-fr")
    {
        using var ctx = parent.CreateContext();
        ctx.Load(ctx.Web, w => w.ServerRelativeUrl, w => w.Url);
        ctx.Load(ctx.Web.Webs, ws => ws.Include(w => w.ServerRelativeUrl, w => w.Url));
        await ctx.ExecuteQueryAsync();

        var expected = $"{ctx.Web.ServerRelativeUrl}/{leaf}";
        var existing = ctx.Web.Webs.AsEnumerable().FirstOrDefault(w =>
            w.ServerRelativeUrl.Equals(expected, StringComparison.OrdinalIgnoreCase));
        string subUrl;
        if (existing != null)
        {
            subUrl = existing.Url;
        }
        else
        {
            var created = ctx.Web.Webs.Add(new WebCreationInformation
            {
                Url = leaf, Title = "Date Locale FR", WebTemplate = "STS#3",
                UseSamePermissionsAsParentSite = true, Language = 1033,
            });
            ctx.Load(created, w => w.Url);
            await ctx.ExecuteQueryAsync();
            subUrl = created.Url;

            // Force the regional locale to French so ValidateUpdateListItem parses dd/MM/yyyy.
            created.RegionalSettings.LocaleId = (uint)lcid;
            created.Update();
            await ctx.ExecuteQueryAsync();
            Console.WriteLine($"  created French subsite {subUrl}");
        }
        return (parent.ForWeb(subUrl), leaf);
    }

    public static async Task DeleteAsync(SpConnection parent, string leaf)
    {
        try
        {
            using var ctx = parent.CreateContext();
            ctx.Load(ctx.Web, w => w.ServerRelativeUrl);
            ctx.Load(ctx.Web.Webs, ws => ws.Include(w => w.ServerRelativeUrl));
            await ctx.ExecuteQueryAsync();
            var expected = $"{ctx.Web.ServerRelativeUrl}/{leaf}";
            var web = ctx.Web.Webs.AsEnumerable().FirstOrDefault(w =>
                w.ServerRelativeUrl.Equals(expected, StringComparison.OrdinalIgnoreCase));
            if (web != null)
            {
                web.DeleteObject();
                await ctx.ExecuteQueryAsync();
                Console.WriteLine($"  deleted subsite {expected}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  (could not delete subsite '{leaf}': {ex.Message})");
        }
    }
}

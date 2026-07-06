using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Probe for review finding M7 (locale-dependent server-message matching). To test it we
/// need a non-English WEB so SharePoint returns localized error messages ("existe deja"
/// instead of "already exists"). This checks whether the tenant can create a French/Spanish
/// web at all (i.e. the language packs are installed), and whether a duplicate-folder error
/// actually comes back localized.
/// </summary>
public static class LocaleProbe
{
    public static async Task RunAsync()
    {
        using var ctx = Program.Source.CreateContext();
        ctx.Load(ctx.Web, w => w.Url);
        await ctx.ExecuteQueryAsync();

        // 1036 = French, 3082 = Spanish (intl sort). Try French first.
        foreach (var (lcid, leaf) in new[] { (1036, "migtestfr"), (3082, "migtestes") })
        {
            Web? web = null;
            try
            {
                web = ctx.Web.Webs.Add(new WebCreationInformation
                {
                    Url = leaf,
                    Title = $"Locale {lcid}",
                    WebTemplate = "STS#3",
                    Language = lcid,
                    UseSamePermissionsAsParentSite = true,
                });
                ctx.Load(web, w => w.Language, w => w.Url);
                await ctx.ExecuteQueryAsync();
                Program.Check($"locale: created web lcid={lcid}", web.Language == (uint)lcid, $"url={web.Url}, lang={web.Language}");
            }
            catch (Exception ex)
            {
                Program.Check($"locale: created web lcid={lcid}", false, ex.Message);
                continue;
            }

            // Trigger the EXACT operation the copy code catches: a generic-list folder item
            // added twice. Does app-only CSOM return a localized ("already exists") message?
            try
            {
                web.Lists.Add(new ListCreationInformation { Title = "LProbe", TemplateType = (int)ListTemplateType.GenericList, Url = "Lists/LProbe" });
                await ctx.ExecuteQueryAsync();
                var lib = web.Lists.GetByTitle("LProbe");
                for (var pass = 1; pass <= 2; pass++)
                {
                    var fi = lib.AddItem(new ListItemCreationInformation { UnderlyingObjectType = FileSystemObjectType.Folder, LeafName = "Dup" });
                    fi["Title"] = "Dup";
                    fi.Update();
                }
                var msg = "(no error)";
                try { await ctx.ExecuteQueryAsync(); }
                catch (Exception dupEx) { msg = dupEx.Message; }
                Console.WriteLine($"  lcid={lcid} duplicate-folder-item message: {msg}");
                Program.Check($"locale: app-only CSOM returns a LOCALIZED (non-English) message, lcid={lcid}",
                    msg != "(no error)" && !msg.Contains("already exists", StringComparison.OrdinalIgnoreCase), msg);
            }
            catch (Exception ex) { Console.WriteLine($"  lcid={lcid} probe error: {ex.Message}"); }
        }
    }
}

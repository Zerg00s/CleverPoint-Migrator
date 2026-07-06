using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Reproduces/verifies review finding M7: the copy engine decided "folder already exists"
/// by matching the English server message. Against a FRENCH web SharePoint returns "existe
/// deja", so a pre-existing folder was recorded Failed instead of Skipped.
///
/// Runs inside the French subweb created by locale-probe. BEFORE the fix the duplicate
/// folder is Failed; AFTER (existence re-check instead of message match) it is Skipped.
/// </summary>
public static class LocaleFolderTest
{
    private const string SrcTitle = "MigTest-M7-Src";
    private const string TgtTitle = "MigTest-M7-Tgt";

    public static async Task RunAsync()
    {
        // The French web from locale-probe.
        var frUrl = Program.Source.SiteUrl.TrimEnd('/') + "/migtestfr";
        var fr = Program.Source.ForWeb(frUrl);

        int lang;
        using (var ctx = fr.CreateContext())
        {
            ctx.Load(ctx.Web, w => w.Language);
            var lists = ctx.Web.Lists;
            ctx.Load(lists, ls => ls.Include(l => l.Title));
            await ctx.ExecuteQueryAsync();
            lang = (int)ctx.Web.Language;
            var have = lists.AsEnumerable().Select(l => l.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var (title, url) in new[] { (SrcTitle, "Lists/MigTestM7Src"), (TgtTitle, "Lists/MigTestM7Tgt") })
                if (!have.Contains(title))
                {
                    ctx.Web.Lists.Add(new ListCreationInformation { Title = title, TemplateType = (int)ListTemplateType.GenericList, Url = url });
                    await ctx.ExecuteQueryAsync();
                }

            // Source has a folder "Shared"; the target ALREADY has "Shared" (the duplicate).
            await EnsureFolderAsync(ctx, SrcTitle, "Shared");
            await EnsureFolderAsync(ctx, TgtTitle, "Shared");
        }
        Program.Check("m7: running against a French web (setup)", lang == 1036, $"web language={lang}");

        var res = await CopyEngine.CopyListAsync(fr, fr, SrcTitle, new CopyOptions { TargetListTitle = TgtTitle });

        var folderRec = res.Records.FirstOrDefault(r => r.ItemType == "Folder");
        Console.WriteLine($"  folder record: {folderRec?.Status} ({folderRec?.Message})");
        // The pre-existing folder must be Skipped, not Failed, despite the French message.
        Program.Check("m7: pre-existing folder is Skipped, not Failed (FAIL = bug)",
            folderRec?.Status == ItemCopyStatus.Skipped, $"{folderRec?.Status}: {folderRec?.Message}");
    }

    private static async Task EnsureFolderAsync(ClientContext ctx, string listTitle, string folder)
    {
        var list = ctx.Web.Lists.GetByTitle(listTitle);
        var items = list.GetItems(CamlQuery.CreateAllItemsQuery());
        ctx.Load(items, its => its.Include(i => i.FileSystemObjectType, i => i["FileLeafRef"]));
        await ctx.ExecuteQueryAsync();
        if (items.AsEnumerable().Any(i => i.FileSystemObjectType == FileSystemObjectType.Folder
                && string.Equals(i["FileLeafRef"]?.ToString(), folder, StringComparison.OrdinalIgnoreCase)))
            return;
        var fi = list.AddItem(new ListItemCreationInformation { UnderlyingObjectType = FileSystemObjectType.Folder, LeafName = folder });
        fi["Title"] = folder;
        fi.Update();
        await ctx.ExecuteQueryAsync();
    }
}

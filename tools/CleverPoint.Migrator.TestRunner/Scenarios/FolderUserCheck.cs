using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Denis reports folders (and only folders) arrive with the wrong
/// Created by / Modified by. Same-site copy, then compare every FOLDER's
/// Author/Editor display names between source and target.
/// </summary>
public static class FolderUserCheck
{
    public static async Task RunAsync()
    {
        var site = Program.TestSite ?? Program.Source.ForWeb($"{Program.Source.SiteUrl}/migtest");
        using (var ctx = site.CreateContext())
        {
            await TestAssets.DeleteIfExistsAsync(ctx, "MigTest-FolderUsers");
        }
        var result = await CopyEngine.CopyListAsync(site, site, TestAssets.SourceLibTitle,
            new CopyOptions { TargetListTitle = "MigTest-FolderUsers", TargetListUrl = "MigTestFolderUsers" });
        Console.WriteLine($"  copy: {result.Summary()}");
        foreach (var r in result.Records.Where(r => r.Status is ItemCopyStatus.Failed or ItemCopyStatus.Warning))
            Console.WriteLine($"    [{r.Status}] {r.ItemType} {r.SourcePath}: {r.Message}");

        static async Task<Dictionary<string, (string Author, string Editor)>> FoldersOf(Core.Csom.SpConnection conn, string title)
        {
            using var ctx = conn.CreateContext();
            var list = ctx.Web.Lists.GetByTitle(title);
            var items = list.GetItems(new CamlQuery { ViewXml = "<View Scope='RecursiveAll'><RowLimit>200</RowLimit></View>" });
            ctx.Load(items);
            await ctx.ExecuteQueryAsync();
            var map = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
            foreach (var i in items.AsEnumerable().Where(i => i.FileSystemObjectType == FileSystemObjectType.Folder))
            {
                var leafPath = ((string)i["FileRef"]).Split('/');
                var key = string.Join('/', leafPath[^2..]);  // parent/leaf, list-root agnostic-ish
                var author = i["Author"] is FieldUserValue a ? a.LookupValue : "(null)";
                var editor = i["Editor"] is FieldUserValue e ? e.LookupValue : "(null)";
                map[(string)i["FileLeafRef"]] = (author, editor);
            }
            return map;
        }

        var src = await FoldersOf(site, TestAssets.SourceLibTitle);
        var tgt = await FoldersOf(site, "MigTest-FolderUsers");
        var bad = 0;
        foreach (var (name, (sa, se)) in src)
        {
            var (ta, te) = tgt.TryGetValue(name, out var t) ? t : ("(missing)", "(missing)");
            var ok = sa == ta && se == te;
            if (!ok) bad++;
            Console.WriteLine($"  folder '{name}': source author='{sa}' editor='{se}'  ->  target author='{ta}' editor='{te}'  {(ok ? "OK" : "<-- WRONG")}");
        }
        Program.Check("folder users preserved on same-site copy", bad == 0, $"{bad} wrong of {src.Count}");
    }
}

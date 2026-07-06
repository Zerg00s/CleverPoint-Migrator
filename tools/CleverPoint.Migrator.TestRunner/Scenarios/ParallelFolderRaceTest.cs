using System.Text;
using CleverPoint.Migrator.Core.Csom;
using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Regression for the F8 parallel folder-creation race: copying a SELECTED set of files that
/// share a nested parent chain (folders NOT in the scan, so created on demand) with
/// ParallelFileTransfers > 1 must not fail with "... already exists" as workers race to create
/// the same folder. Reproduces the reported LMAS "General already exists" failure.
/// </summary>
public static class ParallelFolderRaceTest
{
    public static async Task RunAsync()
    {
        var site = Program.TestSite ?? await TestAssets.EnsureTestSiteAsync(Program.Source);
        Program.TestSite = site;
        const string src = "MigTest-PFRace";
        const string tgt = "MigTest-PFRaceCopy";

        // 6 files all inside General/Knowledge Hub/Renewals so every worker races the same chain.
        var filePaths = new List<string>();
        using (var ctx = site.CreateContext())
        {
            var deleted = await TestAssets.DeleteIfExistsAsync(ctx, src);
            await TestAssets.DeleteIfExistsAsync(ctx, tgt);
            List lib;
            if (deleted)
                lib = ctx.Web.Lists.Add(new ListCreationInformation
                {
                    Title = src, TemplateType = (int)ListTemplateType.DocumentLibrary, Url = "MigTestPFRace",
                });
            else { lib = ctx.Web.Lists.GetByTitle(src); await TestAssets.ClearListAsync(ctx, lib); }
            ctx.Load(lib, l => l.RootFolder.ServerRelativeUrl);
            await ctx.ExecuteWithRetryAsync();

            var f1 = lib.RootFolder.Folders.Add("General");
            var f2 = f1.Folders.Add("Knowledge Hub");
            var f3 = f2.Folders.Add("Renewals");
            await ctx.ExecuteWithRetryAsync();

            var root = lib.RootFolder.ServerRelativeUrl;
            for (var i = 0; i < 6; i++)
            {
                f3.Files.Add(new FileCreationInformation
                {
                    Url = $"renewal-{i}.txt",
                    Content = Encoding.UTF8.GetBytes($"renewal file {i} - unique content {Guid.Empty}{i}"),
                    Overwrite = true,
                });
                filePaths.Add($"{root}/General/Knowledge Hub/Renewals/renewal-{i}.txt");
            }
            await ctx.ExecuteWithRetryAsync();
        }
        Program.Check("pf-race: 6 source files provisioned in a nested folder", filePaths.Count == 6, "setup");

        // Copy just those files, into a fresh target library, WITH parallelism.
        var res = await CopyEngine.CopyListAsync(site, site, src, new CopyOptions
        {
            TargetListTitle = tgt,
            TargetListUrl = "MigTestPFRaceCopy",
            SelectedPaths = filePaths,
            ParallelFileTransfers = 4,
        });

        foreach (var r in res.Records.Where(r => r.Status == ItemCopyStatus.Failed))
            Console.WriteLine($"    FAILED {r.ItemType} {r.SourcePath}: {r.Message}");
        Console.WriteLine($"  {res.Summary()}");
        Program.Check("pf-race: no 'already exists' folder race failures", res.Failed == 0, res.Summary());

        var copied = res.Records.Count(r => r.ItemType == "File" && r.Status == ItemCopyStatus.Copied);
        Program.Check("pf-race: all 6 files copied", copied == 6, $"{copied}/6");

        // The shared folder chain exists exactly once on the target.
        using (var ctx = site.CreateContext())
        {
            var t = ctx.Web.Lists.GetByTitle(tgt);
            ctx.Load(t, l => l.ItemCount);
            await ctx.ExecuteWithRetryAsync();
            // 6 files + 3 folders (General, Knowledge Hub, Renewals).
            Program.Check("pf-race: target has 6 files + 3 folders, no dupes", t.ItemCount == 9, $"{t.ItemCount} items");
        }
    }
}

using CleverPoint.Migrator.Core.MigrationApi;
using CleverPoint.Migrator.Core.Model;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Reproduces review finding C4: when a Migration-API import job fails at the
/// JOB level, the engine still logs every file in that job as Copied, so resume
/// and delta runs treat files that never landed as done.
///
/// It forces a job-level fatal by putting an XML-1.0-illegal control character
/// (vertical tab, U+000B) into a Note column value. MigrationPackageBuilder emits
/// text fields through SecurityElement.Escape, which does NOT strip control chars,
/// so the manifest is not well-formed and SharePoint fatals the whole import job.
///
/// Expected WHILE THE BUG IS PRESENT:
///   the import job fails, the target library is EMPTY, yet the run reports the
///   files Copied -> the "files not falsely Copied" check FAILS (that is the proof).
/// After the fix, a failed job's files should be Failed (or the run should not
/// claim Copied), so that check PASSES.
/// </summary>
public static class ApiJobFailTest
{
    private const string SrcTitle = "MigTest-C4-Src";
    private const string TgtTitle = "MigTest-C4-Tgt";

    public static async Task RunAsync()
    {
        var site = Program.TestSite ?? await TestAssets.EnsureTestSiteAsync(Program.Source);
        Program.TestSite = site;

        // ---- Source library with a Note column + two files whose Notes value
        //      carries a vertical tab that will break the package manifest. ----
        string webUrl, srcRoot;
        using (var ctx = site.CreateContext())
        {
            ctx.Load(ctx.Web, w => w.Url);
            var lists = ctx.Web.Lists;
            ctx.Load(lists, ls => ls.Include(l => l.Title));
            await ctx.ExecuteQueryAsync();
            webUrl = ctx.Web.Url.TrimEnd('/');
            List lib;
            if (!lists.AsEnumerable().Any(l => l.Title == SrcTitle))
            {
                ctx.Web.Lists.Add(new ListCreationInformation
                {
                    Title = SrcTitle,
                    TemplateType = (int)ListTemplateType.DocumentLibrary,
                    Url = "MigTestC4Src",
                });
                await ctx.ExecuteQueryAsync();
                lib = ctx.Web.Lists.GetByTitle(SrcTitle);
                lib.Fields.AddFieldAsXml("<Field Type='Note' DisplayName='Notes' Name='Notes'/>",
                    true, AddFieldOptions.DefaultValue);
                await ctx.ExecuteQueryAsync();
            }
            else
            {
                lib = ctx.Web.Lists.GetByTitle(SrcTitle);
            }
            ctx.Load(lib.RootFolder, f => f.ServerRelativeUrl);
            await ctx.ExecuteQueryAsync();
            srcRoot = lib.RootFolder.ServerRelativeUrl;
        }

        var badNote = "before\u000Bafter";   // U+000B vertical tab: illegal in XML 1.0
        foreach (var name in new[] { "c4-a.bin", "c4-b.bin" })
        {
            var buf = new byte[500];
            new Random(name.GetHashCode()).NextBytes(buf);
            await site.Rest.PostBinaryAsync(
                $"{webUrl}/_api/web/GetFolderByServerRelativeUrl('{srcRoot.Replace("'", "''")}')/Files/add(url='{name}',overwrite=true)",
                buf, buf.Length);
            using var ctx = site.CreateContext();
            var item = ctx.Web.GetFileByServerRelativeUrl($"{srcRoot}/{name}").ListItemAllFields;
            try
            {
                item["Notes"] = badNote;
                item.Update();
                await ctx.ExecuteQueryAsync();
            }
            catch (Exception ex) when (ex.Message.Contains("0x0B") || ex.Message.Contains("invalid character"))
            {
                // Side-finding: CSOM serializes the value to XML client-side and refuses to
                // write a control char, so the M13 "control char breaks the manifest" vector
                // cannot be introduced through a normal CSOM write. This closes the easy path
                // to force a job-level fatal for the C4 repro.
                Program.Check("c4: CSOM blocks control-char write (M13 is hard to trigger)", true, ex.Message);
                Console.WriteLine("  C4 live repro via control-char injection is not possible: CSOM guards the write.");
                Console.WriteLine("  C4 remains code-verified (emit loop logs Copied on job/timeout failure); a real");
                Console.WriteLine("  job-level fatal cannot be forced from outside the engine without a test seam.");
                return;
            }
        }
        Program.Check("c4: two source files staged with a control-char Note", true, "c4-a.bin, c4-b.bin");

        // ---- Copy through the Migration API engine ----
        using (var ctx = site.CreateContext()) await TestAssets.DeleteIfExistsAsync(ctx, TgtTitle);
        var engine = new MigrationApiEngine(site, site);
        engine.OnProgress += msg => Console.WriteLine($"  [api] {msg}");
        var result = await engine.CopyLibraryAsync(SrcTitle, new CopyOptions
        {
            TargetListTitle = TgtTitle,
            TargetListUrl = "MigTestC4Tgt",
        });

        var fileCopied = result.Records.Count(r => r.ItemType == "File" && r.Status == ItemCopyStatus.Copied);
        var fileFailed = result.Records.Count(r => r.ItemType == "File" && r.Status == ItemCopyStatus.Failed);
        var jobFailed = result.Records.Count(r => r.ItemType == "Job" && r.Status == ItemCopyStatus.Failed);
        Console.WriteLine($"  result: {result.Summary()}");
        Console.WriteLine($"  File records -> Copied={fileCopied}, Failed={fileFailed}; Job Failed rows={jobFailed}");

        long tgtCount = -1;
        try
        {
            using var ctx = site.CreateContext();
            var l = ctx.Web.Lists.GetByTitle(TgtTitle);
            ctx.Load(l, x => x.ItemCount);
            await ctx.ExecuteQueryAsync();
            tgtCount = l.ItemCount;
        }
        catch { /* list may not exist if it failed very early */ }
        Console.WriteLine($"  ACTUAL target item count: {tgtCount} (expected 2 if the import really succeeded)");

        // The import job must actually have failed for this test to mean anything.
        var jobDidFail = jobFailed > 0 || tgtCount == 0;
        Program.Check("c4: the import job actually failed (setup)", jobDidFail,
            $"jobFailedRows={jobFailed}, targetItems={tgtCount}");

        // The finding: on a job-level failure the files are still logged Copied while the
        // target is empty. This assertion FAILS while the bug is present -> that is the proof.
        Program.Check("c4: files NOT falsely reported Copied on a failed job (FAIL = bug confirmed)",
            !(jobDidFail && fileCopied > 0),
            $"reported {fileCopied} file(s) Copied but target has {tgtCount} item(s)");
    }
}

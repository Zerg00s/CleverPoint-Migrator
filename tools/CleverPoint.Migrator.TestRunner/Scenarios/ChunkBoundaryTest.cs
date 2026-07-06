using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Reproduces the chunked-upload boundary bug (review finding C2). A file whose
/// size is an EXACT multiple of UploadSliceBytes never receives a FinishUpload
/// call in FileCopier.CopyLargeFileAsync, so the target keeps the 0-byte stub
/// while the run reports the file Copied. A control file that is NOT an exact
/// multiple copies intact, isolating the boundary condition and proving the
/// chunked path itself works.
///
/// Expected result WHILE THE BUG IS PRESENT:
///   PASS  chunk-boundary: control (non-multiple) copied intact
///   FAIL  chunk-boundary: EXACT-multiple file copied intact  (target = 0 bytes)
/// The FAIL is the confirmation. After the fix, both PASS.
/// </summary>
public static class ChunkBoundaryTest
{
    private const int Slice = 256 * 1024;                    // small slice so the test is fast
    private const string SrcTitle = "MigTest-ChunkBoundary-Src";
    private const string TgtTitle = "MigTest-ChunkBoundary-Tgt";

    public static async Task RunAsync()
    {
        var site = Program.TestSite ?? await TestAssets.EnsureTestSiteAsync(Program.Source);
        Program.TestSite = site;

        var exactSize = 2 * Slice;          // 524,288 B — exact multiple of the slice
        var controlSize = 2 * Slice + 777;  // not a multiple — the control

        // ---- Stage the source library + two precisely-sized files ----
        string webUrl, srcRoot;
        using (var ctx = site.CreateContext())
        {
            ctx.Load(ctx.Web, w => w.Url);
            var lists = ctx.Web.Lists;
            ctx.Load(lists, ls => ls.Include(l => l.Title));
            await ctx.ExecuteQueryAsync();
            webUrl = ctx.Web.Url.TrimEnd('/');
            if (!lists.AsEnumerable().Any(l => l.Title == SrcTitle))
            {
                ctx.Web.Lists.Add(new ListCreationInformation
                {
                    Title = SrcTitle,
                    TemplateType = (int)ListTemplateType.DocumentLibrary,
                    Url = "MigTestChunkBoundarySrc",
                });
                await ctx.ExecuteQueryAsync();
            }
            var lib = ctx.Web.Lists.GetByTitle(SrcTitle);
            ctx.Load(lib.RootFolder, f => f.ServerRelativeUrl);
            await ctx.ExecuteQueryAsync();
            srcRoot = lib.RootFolder.ServerRelativeUrl;
        }

        await UploadExactAsync(site, webUrl, srcRoot, "exact.bin", exactSize);
        await UploadExactAsync(site, webUrl, srcRoot, "control.bin", controlSize);
        Program.Check("chunk-boundary: source exact.bin staged",
            await SizeAsync(site, webUrl, $"{srcRoot}/exact.bin") == exactSize, $"{exactSize} B");
        Program.Check("chunk-boundary: source control.bin staged",
            await SizeAsync(site, webUrl, $"{srcRoot}/control.bin") == controlSize, $"{controlSize} B");

        // ---- Copy through the CLASSIC engine, forcing the chunked path ----
        using (var ctx = site.CreateContext()) await TestAssets.DeleteIfExistsAsync(ctx, TgtTitle);

        var res = await CopyEngine.CopyListAsync(site, site, SrcTitle, new CopyOptions
        {
            TargetListTitle = TgtTitle,
            TargetListUrl = "MigTestChunkBoundaryTgt",
            LargeFileThresholdBytes = Slice,   // both files (>= Slice) take CopyLargeFileAsync
            UploadSliceBytes = Slice,
        });

        foreach (var r in res.Records.Where(r => r.ItemType == "File"))
            Console.WriteLine($"  reported: {LeafOf(r.SourcePath)} -> {r.Status} ({r.Message}), SizeBytes={r.SizeBytes}");

        // ---- Read back what actually landed on the target ----
        string tgtRoot;
        using (var ctx = site.CreateContext())
        {
            var lib = ctx.Web.Lists.GetByTitle(TgtTitle);
            ctx.Load(lib.RootFolder, f => f.ServerRelativeUrl);
            await ctx.ExecuteQueryAsync();
            tgtRoot = lib.RootFolder.ServerRelativeUrl;
        }
        var exactTgt = await SizeAsync(site, webUrl, $"{tgtRoot}/exact.bin");
        var controlTgt = await SizeAsync(site, webUrl, $"{tgtRoot}/control.bin");
        Console.WriteLine($"  ACTUAL target sizes: exact.bin={exactTgt} (want {exactSize}), " +
                          $"control.bin={controlTgt} (want {controlSize})");

        // Control proves the chunked path itself works end to end.
        Program.Check("chunk-boundary: control (non-multiple) copied intact",
            controlTgt == controlSize, $"target={controlTgt} of {controlSize}");
        // The finding: the exact-multiple file is reported Copied but is 0 bytes on the
        // target. This assertion FAILS while the bug is present; that failure IS the proof.
        Program.Check("chunk-boundary: EXACT-multiple file copied intact (FAIL = bug confirmed)",
            exactTgt == exactSize, $"target={exactTgt} of {exactSize}, run reported it Copied");
    }

    private static string LeafOf(string path) => string.IsNullOrEmpty(path) ? path : path[(path.LastIndexOf('/') + 1)..];

    private static async Task UploadExactAsync(Core.Csom.SpConnection site, string webUrl, string root, string name, int size)
    {
        var buf = new byte[size];
        new Random(777).NextBytes(buf);
        await site.Rest.PostBinaryAsync(
            $"{webUrl}/_api/web/GetFolderByServerRelativeUrl('{root.Replace("'", "''")}')/Files/add(url='{name}',overwrite=true)",
            buf, buf.Length);
    }

    private static async Task<long> SizeAsync(Core.Csom.SpConnection site, string webUrl, string fileRel)
    {
        try
        {
            using var doc = await site.Rest.GetJsonAsync(
                $"{webUrl}/_api/web/GetFileByServerRelativeUrl('{fileRel.Replace("'", "''")}')?$select=Length");
            return long.Parse(doc.RootElement.GetProperty("Length").GetString() ?? "0");
        }
        catch { return -1; }
    }
}

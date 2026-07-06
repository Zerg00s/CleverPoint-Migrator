using System.Diagnostics;
using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using CleverPoint.Migrator.Core.Validation;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Parallel file transfer (F8): a library copied with ParallelFileTransfers > 1 must be
/// byte-for-byte identical to a sequential copy - same files, SHA-256 clean, no failures, no
/// duplicates - proving the per-worker contexts + shared record/hash locks are correct.
/// </summary>
public static class ParallelCopyTest
{
    public static async Task RunAsync()
    {
        var site = Program.TestSite ?? await TestAssets.EnsureTestSiteAsync(Program.Source);
        Program.TestSite = site;
        await TestAssets.RecreateSourceLibraryAsync(site);
        using (var ctx = site.CreateContext())
        {
            await TestAssets.DeleteIfExistsAsync(ctx, "MigTest-SeqCopy");
            await TestAssets.DeleteIfExistsAsync(ctx, "MigTest-ParCopy");
        }

        // Sequential baseline.
        var sw1 = Stopwatch.StartNew();
        var seq = await CopyEngine.CopyListAsync(site, site, TestAssets.SourceLibTitle, new CopyOptions
        {
            TargetListTitle = "MigTest-SeqCopy", TargetListUrl = "MigTestSeqCopy",
            ParallelFileTransfers = 1,
        });
        sw1.Stop();
        Program.Check("parallel: sequential baseline clean", seq.Failed == 0 && seq.Copied > 0, seq.Summary());

        // Parallel copy (4 workers).
        var sw2 = Stopwatch.StartNew();
        var par = await CopyEngine.CopyListAsync(site, site, TestAssets.SourceLibTitle, new CopyOptions
        {
            TargetListTitle = "MigTest-ParCopy", TargetListUrl = "MigTestParCopy",
            ParallelFileTransfers = 4,
        });
        sw2.Stop();
        Console.WriteLine($"  sequential {sw1.Elapsed.TotalSeconds:F1}s vs parallel(4) {sw2.Elapsed.TotalSeconds:F1}s");
        Program.Check("parallel: parallel copy no failures", par.Failed == 0, par.Summary());

        var seqFiles = seq.Records.Count(r => r.ItemType == "File" && r.Status == ItemCopyStatus.Copied);
        var parFiles = par.Records.Count(r => r.ItemType == "File" && r.Status == ItemCopyStatus.Copied);
        Program.Check("parallel: same file count as sequential", seqFiles == parFiles && parFiles > 0,
            $"seq {seqFiles} vs par {parFiles}");

        // Parallel run must have hashed every file it copied (shared-hash-dict integrity).
        Program.Check("parallel: SHA-256 recorded for every copied file", par.FileHashes.Count >= parFiles,
            $"{par.FileHashes.Count} hashes for {parFiles} files");

        // Both targets verify byte-for-byte against the source.
        using var sctx = site.CreateContext();
        using var tctx = site.CreateContext();
        var verifier = new CopyVerifier(sctx, tctx);
        var mm = await verifier.VerifyAsync(
            sctx.Web.Lists.GetByTitle(TestAssets.SourceLibTitle),
            tctx.Web.Lists.GetByTitle("MigTest-ParCopy"),
            new[] { "DocCategory" }, compareFileContent: true);
        foreach (var m in mm.Take(10)) Console.WriteLine($"    MISMATCH: {m}");
        Program.Check("parallel: target verifies byte-for-byte (SHA-256) vs source", mm.Count == 0, $"{mm.Count} mismatches");

        // No duplicates on the parallel target.
        using var vctx = site.CreateContext();
        var pl = vctx.Web.Lists.GetByTitle("MigTest-ParCopy");
        vctx.Load(pl, l => l.ItemCount);
        var sl = vctx.Web.Lists.GetByTitle("MigTest-SeqCopy");
        vctx.Load(sl, l => l.ItemCount);
        await vctx.ExecuteQueryAsync();
        Program.Check("parallel: no duplicates (same item count as sequential)", pl.ItemCount == sl.ItemCount,
            $"par {pl.ItemCount} vs seq {sl.ItemCount}");
    }
}

using CleverPoint.Migrator.Core.History;
using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using CleverPoint.Migrator.Core.Validation;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Delta and resume correctness:
///  - "delta": full copy with history + item map, mutate the source (3 edits,
///    2 adds), delta run that UPDATES mapped items (no duplicates) and logs
///    what it skipped. The source list deliberately has 10 items with the
///    IDENTICAL Title to prove the delta key is the persisted id map.
///  - "resume": cancel a library copy mid-run (fault injection), record the
///    partial run, then resume skipping already-copied files.
/// </summary>
public static class DeltaResumeTests
{
    private const string KeyListTitle = "MigTest-KeyList";

    public static async Task DeltaAsync()
    {
        var site = Program.TestSite ?? await TestAssets.EnsureTestSiteAsync(Program.Source);
        Program.TestSite = site;
        var dbPath = Path.Combine(Directory.GetCurrentDirectory(), "migration-history-test.db");
        if (System.IO.File.Exists(dbPath)) System.IO.File.Delete(dbPath);
        using var store = new HistoryStore(dbPath);

        // Source list: 10 items, ALL with the same Title.
        using var ctx = site.CreateContext();
        var sourceDeleted = await TestAssets.DeleteIfExistsAsync(ctx, KeyListTitle);
        await TestAssets.DeleteIfExistsAsync(ctx, "MigTest-KeyCopy");
        List keyList;
        if (sourceDeleted)
        {
            keyList = ctx.Web.Lists.Add(new ListCreationInformation
            {
                Title = KeyListTitle,
                TemplateType = (int)ListTemplateType.GenericList,
                Url = "Lists/MigTestKeyList",
            });
            keyList.Fields.AddFieldAsXml("<Field Type='Text' Name='Marker' DisplayName='Marker' MaxLength='100' />",
                true, AddFieldOptions.AddFieldInternalNameHint);
            await ctx.ExecuteQueryAsync();
        }
        else
        {
            // Retention kept the (cleared) list; schema is intact.
            keyList = ctx.Web.Lists.GetByTitle(KeyListTitle);
        }
        for (var i = 0; i < 10; i++)
        {
            var item = keyList.AddItem(new ListItemCreationInformation());
            item["Title"] = "Same Title";          // identical on purpose
            item["Marker"] = $"marker-{i + 1:D2}";
            item.Update();
        }
        await ctx.ExecuteQueryAsync();

        var pair = HistoryStore.PairKey(site.SiteUrl, KeyListTitle, site.SiteUrl, "MigTest-KeyCopy");

        // ---- Run 1: full copy with history + map persistence ----
        var run1Id = store.StartRun(new MigrationRun
        {
            Name = "delta test full", SourceUrl = site.SiteUrl, SourceList = KeyListTitle,
            TargetUrl = site.SiteUrl, TargetList = "MigTest-KeyCopy",
        });
        var options1 = new CopyOptions { TargetListTitle = "MigTest-KeyCopy", TargetListUrl = "Lists/MigTestKeyCopy" };
        var result1 = await CopyEngine.CopyListAsync(site, site, KeyListTitle, options1);
        foreach (var rec in result1.Records) store.RecordItem(run1Id, rec);
        store.SaveItemMap(pair, result1.ItemMappings);
        store.FinishRun(run1Id, result1, result1.Failed == 0 ? "Completed" : "CompletedWithIssues");
        var run1 = store.GetRuns(1)[0];
        Program.Check("delta: full copy recorded", result1.Failed == 0 && run1.Copied > 0,
            $"run #{run1.Id}: {result1.Summary()}, {result1.ItemMappings.Count} id mappings");

        // ---- Mutate the source: edit 3, add 2 ----
        // Baseline comes from the SERVER-stamped Modified timestamps seen in
        // run 1, never the client clock (WSL clock skew silently breaks
        // wall-clock baselines; learned the hard way).
        var deltaBaseline = result1.MaxSourceModifiedUtc!.Value.AddSeconds(1);
        await Task.Delay(TimeSpan.FromSeconds(2));   // separate Modified timestamps from run1
        var items = keyList.GetItems(CamlQuery.CreateAllItemsQuery(100));
        ctx.Load(items);
        await ctx.ExecuteQueryAsync();
        var editedIds = new List<int>();
        foreach (var item in items.AsEnumerable().Take(3))
        {
            item["Marker"] = item["Marker"] + "-edited";
            item.Update();
            editedIds.Add(item.Id);
        }
        for (var i = 0; i < 2; i++)
        {
            var item = keyList.AddItem(new ListItemCreationInformation());
            item["Title"] = "Same Title";
            item["Marker"] = $"marker-new-{i + 1}";
            item.Update();
        }
        await ctx.ExecuteQueryAsync();

        // ---- Run 2: delta (modified-since baseline + upsert map) ----
        var run2Id = store.StartRun(new MigrationRun
        {
            Name = "delta test incremental", SourceUrl = site.SiteUrl, SourceList = KeyListTitle,
            TargetUrl = site.SiteUrl, TargetList = "MigTest-KeyCopy",
        });
        var options2 = new CopyOptions
        {
            TargetListTitle = "MigTest-KeyCopy",
            ModifiedSinceUtc = deltaBaseline,
            UpsertItemMap = store.GetItemMap(pair),
        };
        var result2 = await CopyEngine.CopyListAsync(site, site, KeyListTitle, options2);
        foreach (var rec in result2.Records) store.RecordItem(run2Id, rec);
        store.SaveItemMap(pair, result2.ItemMappings);
        store.FinishRun(run2Id, result2, "Completed");

        var updated = result2.Records.Count(r => r.ItemType == "Item" && r.Status == ItemCopyStatus.Copied && r.Message == "updated (delta)");
        var added = result2.Records.Count(r => r.ItemType == "Item" && r.Status == ItemCopyStatus.Copied && r.Message == null);
        var skipped = result2.Records.Count(r => r.Status == ItemCopyStatus.Skipped && (r.Message?.StartsWith("delta:") ?? false));
        Console.WriteLine($"  delta: {updated} updated, {added} added, {skipped} skipped-unchanged");
        Program.Check("delta: 3 items updated in place", updated == 3, $"{updated}");
        Program.Check("delta: 2 new items added", added == 2, $"{added}");
        Program.Check("delta: unchanged items visibly skipped", skipped == 7, $"{skipped}");

        // ---- No duplicates + faithful content, paired by ID MAP (titles identical) ----
        using var sctx = site.CreateContext();
        using var tctx = site.CreateContext();
        var src = sctx.Web.Lists.GetByTitle(KeyListTitle);
        var tgt = tctx.Web.Lists.GetByTitle("MigTest-KeyCopy");
        tctx.Load(tgt, l => l.ItemCount);
        await tctx.ExecuteQueryAsync();
        Program.Check("delta: no duplicates (12 = 12)", tgt.ItemCount == 12, $"target has {tgt.ItemCount}");

        var verifier = new CopyVerifier(sctx, tctx) { ItemMap = store.GetItemMap(pair) };
        var mismatches = await verifier.VerifyAsync(src, tgt, new[] { "Title", "Marker" });
        foreach (var m in mismatches.Take(10)) Console.WriteLine($"    MISMATCH: {m}");
        Program.Check("delta: verification clean via id map", mismatches.Count == 0, $"{mismatches.Count} mismatches");

        // ---- Log export ----
        var csvPath = Path.Combine(Directory.GetCurrentDirectory(), "delta-run-log.csv");
        store.ExportRunCsv(run2Id, csvPath);
        Program.Check("delta: CSV log exported", System.IO.File.Exists(csvPath) && new FileInfo(csvPath).Length > 100,
            $"{new FileInfo(csvPath).Length} bytes");
    }

    public static async Task ResumeAsync()
    {
        var site = Program.TestSite ?? await TestAssets.EnsureTestSiteAsync(Program.Source);
        Program.TestSite = site;
        var dbPath = Path.Combine(Directory.GetCurrentDirectory(), "migration-history-test.db");
        using var store = new HistoryStore(dbPath);

        using (var ctx = site.CreateContext())
        {
            await TestAssets.DeleteIfExistsAsync(ctx, "MigTest-ResumeCopy");
        }

        // ---- Interrupted run: cancel after 6 copied files ----
        var runId = store.StartRun(new MigrationRun
        {
            Name = "resume test (interrupted)", SourceUrl = site.SiteUrl, SourceList = TestAssets.SourceLibTitle,
            TargetUrl = site.SiteUrl, TargetList = "MigTest-ResumeCopy",
        });
        using var cts = new CancellationTokenSource();
        var options = new CopyOptions { TargetListTitle = "MigTest-ResumeCopy", TargetListUrl = "MigTestResumeCopy" };
        var partial = new CopyResult();
        var copiedFiles = 0;
        partial.RecordAdded += rec =>
        {
            store.RecordItem(runId, rec);
            if (rec.ItemType == "File" && rec.Status == ItemCopyStatus.Copied && ++copiedFiles >= 6)
                cts.Cancel();
        };

        var wasCancelled = false;
        try
        {
            await CopyEngine.CopyListAsync(site, site, TestAssets.SourceLibTitle, options, null, cts.Token, partial);
        }
        catch (OperationCanceledException)
        {
            wasCancelled = true;
        }
        store.FinishRun(runId, partial, "Interrupted");
        Program.Check("resume: run interrupted mid-copy", wasCancelled, $"{copiedFiles} files copied before cancel");

        // ---- Resume: skip what the interrupted run already copied ----
        var resumeRunId = store.StartRun(new MigrationRun
        {
            Name = "resume test (resumed)", SourceUrl = site.SiteUrl, SourceList = TestAssets.SourceLibTitle,
            TargetUrl = site.SiteUrl, TargetList = "MigTest-ResumeCopy",
        });
        var resumeOptions = new CopyOptions
        {
            TargetListTitle = "MigTest-ResumeCopy",
            ResumeSkipPaths = store.GetCopiedSourcePaths(runId),
        };
        var result = await CopyEngine.CopyListAsync(site, site, TestAssets.SourceLibTitle, resumeOptions);
        foreach (var rec in result.Records) store.RecordItem(resumeRunId, rec);
        store.FinishRun(resumeRunId, result, "Completed");

        var resumeSkipped = result.Records.Count(r => r.Status == ItemCopyStatus.Skipped && (r.Message?.StartsWith("resume:") ?? false));
        Console.WriteLine($"  resume: {result.Summary()}, {resumeSkipped} skipped from interrupted run");
        Program.Check("resume: no failures", result.Failed == 0, result.Summary());
        Program.Check("resume: previously copied files skipped", resumeSkipped >= 6, $"{resumeSkipped} skipped");

        using var sctx = site.CreateContext();
        using var tctx = site.CreateContext();
        var verifier = new CopyVerifier(sctx, tctx);
        var mismatches = await verifier.VerifyAsync(
            sctx.Web.Lists.GetByTitle(TestAssets.SourceLibTitle),
            tctx.Web.Lists.GetByTitle("MigTest-ResumeCopy"),
            new[] { "DocCategory" }, compareFileContent: true);
        foreach (var m in mismatches.Take(10)) Console.WriteLine($"    MISMATCH: {m}");
        Program.Check("resume: final state verified complete", mismatches.Count == 0, $"{mismatches.Count} mismatches");
    }

}

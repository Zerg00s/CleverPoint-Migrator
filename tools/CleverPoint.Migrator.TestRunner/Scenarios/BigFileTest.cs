using System.Diagnostics;
using System.Security.Cryptography;
using CleverPoint.Migrator.Core.MigrationApi;
using CleverPoint.Migrator.Core.Model;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Large-file end-to-end: generates a ~1.2 GB file straight into the source
/// library via a chunked upload session (seeded data, streamed, no local
/// disk), then copies the library through the Migration API engine with the
/// hybrid large-file route, and verifies by streaming SHA-256 on both sides.
/// The same code path serves any size up to SPO's 250 GB limit.
/// </summary>
public static class BigFileTest
{
    public const string LibTitle = "MigTest-HugeSrc";
    private const string FileName = "huge-01.bin";
    private const long TargetSize = 1_200_000_000;
    private const int Slice = 10 * 1024 * 1024;

    public static async Task RunAsync()
    {
        var site = Program.TestSite ?? await TestAssets.EnsureTestSiteAsync(Program.Source);
        Program.TestSite = site;

        using var ctx = site.CreateContext();
        var lists = ctx.Web.Lists;
        ctx.Load(lists, ls => ls.Include(l => l.Title));
        ctx.Load(ctx.Web, w => w.Url, w => w.ServerRelativeUrl);
        await ctx.ExecuteQueryAsync();
        var webUrl = ctx.Web.Url.TrimEnd('/');

        if (!lists.AsEnumerable().Any(l => l.Title == LibTitle))
        {
            ctx.Web.Lists.Add(new ListCreationInformation
            {
                Title = LibTitle,
                TemplateType = (int)ListTemplateType.DocumentLibrary,
                Url = "MigTestHugeSrc",
            });
            await ctx.ExecuteQueryAsync();
        }
        var srcLib = ctx.Web.Lists.GetByTitle(LibTitle);
        ctx.Load(srcLib.RootFolder, f => f.ServerRelativeUrl);
        await ctx.ExecuteQueryAsync();
        var root = srcLib.RootFolder.ServerRelativeUrl;
        var fileRel = $"{root}/{FileName}";

        // ---- Provision the huge file (skip when present at full size) ----
        var sizeNow = await GetFileSizeAsync(site, webUrl, fileRel);
        if (sizeNow != TargetSize)
        {
            Console.WriteLine($"  generating {TargetSize / 1048576} MB into {fileRel} (chunked upload, no local disk)");
            await Program.Source.Rest.PostAsync(
                $"{webUrl}/_api/web/GetFolderByServerRelativeUrl('{root.Replace("'", "''")}')/Files/add(url='{FileName}',overwrite=true)");

            var uploadId = Guid.NewGuid();
            var rng = new Random(12345);
            var slice = new byte[Slice];
            long offset = 0;
            var sw = Stopwatch.StartNew();
            var endpoint = $"{webUrl}/_api/web/GetFileByServerRelativeUrl('{fileRel.Replace("'", "''")}')";
            while (offset < TargetSize)
            {
                var count = (int)Math.Min(Slice, TargetSize - offset);
                rng.NextBytes(slice);
                if (offset == 0)
                    await Program.Source.Rest.PostBinaryAsync($"{endpoint}/StartUpload(uploadId=guid'{uploadId}')", slice, count);
                else if (offset + count >= TargetSize)
                    await Program.Source.Rest.PostBinaryAsync($"{endpoint}/FinishUpload(uploadId=guid'{uploadId}',fileOffset={offset})", slice, count);
                else
                    await Program.Source.Rest.PostBinaryAsync($"{endpoint}/ContinueUpload(uploadId=guid'{uploadId}',fileOffset={offset})", slice, count);
                offset += count;
                if (offset % (100 * 1048576) < Slice)
                    Console.WriteLine($"  uploaded {offset / 1048576} MB ({offset / 1048576.0 / Math.Max(1, sw.Elapsed.TotalSeconds):F1} MB/s)");
            }
            Console.WriteLine($"  generation done in {sw.Elapsed.TotalMinutes:F1} min");
        }
        Program.Check("bigfile: source file present", await GetFileSizeAsync(site, webUrl, fileRel) == TargetSize, $"{TargetSize / 1048576} MB");

        // ---- Copy via Migration API engine (hybrid route kicks in) ----
        using (var dctx = site.CreateContext())
        {
            await TestAssets.DeleteIfExistsAsync(dctx, "MigTest-HugeCopy");
        }
        var engine = new MigrationApiEngine(site, site);
        engine.OnProgress += msg => Console.WriteLine($"  [api] {msg}");
        var memBefore = Process.GetCurrentProcess().WorkingSet64;
        var sw2 = Stopwatch.StartNew();
        var result = await engine.CopyLibraryAsync(LibTitle, new CopyOptions
        {
            TargetListTitle = "MigTest-HugeCopy",
            TargetListUrl = "MigTestHugeCopy",
        });
        sw2.Stop();
        var memAfter = Process.GetCurrentProcess().WorkingSet64;
        Console.WriteLine($"  copy took {sw2.Elapsed.TotalMinutes:F1} min; working set {memBefore / 1048576} -> {memAfter / 1048576} MB");
        foreach (var r in result.Records.Where(r => r.Status == ItemCopyStatus.Failed).Take(5))
            Console.WriteLine($"    [Failed] {r.ItemType}: {r.Message}");
        Program.Check("bigfile: copy no failures", result.Failed == 0, result.Summary());
        Program.Check("bigfile: memory stayed flat", memAfter - memBefore < 500 * 1048576, $"grew {(memAfter - memBefore) / 1048576} MB");

        // ---- Verify: streaming hash both sides ----
        using var tctx = site.CreateContext();
        var targetLib = tctx.Web.Lists.GetByTitle("MigTest-HugeCopy");
        tctx.Load(targetLib.RootFolder, f => f.ServerRelativeUrl);
        await tctx.ExecuteQueryAsync();
        var targetRel = $"{targetLib.RootFolder.ServerRelativeUrl}/{FileName}";

        var srcHash = await StreamingHashAsync(webUrl, fileRel);
        var tgtHash = await StreamingHashAsync(webUrl, targetRel);
        Program.Check("bigfile: streaming SHA-256 match", srcHash == tgtHash, $"{srcHash[..16]}...");
    }

    private static async Task<long> GetFileSizeAsync(Core.Csom.SpConnection site, string webUrl, string fileRel)
    {
        try
        {
            using var doc = await site.Rest.GetJsonAsync(
                $"{webUrl}/_api/web/GetFileByServerRelativeUrl('{fileRel.Replace("'", "''")}')?$select=Length");
            return long.Parse(doc.RootElement.GetProperty("Length").GetString() ?? "0");
        }
        catch
        {
            return -1;
        }
    }

    private static async Task<string> StreamingHashAsync(string webUrl, string fileRel)
    {
        await using var stream = await Program.Source.Rest.GetStreamAsync(
            $"{webUrl}/_api/web/GetFileByServerRelativeUrl('{fileRel.Replace("'", "''")}')/$value");
        using var sha = SHA256.Create();
        var buffer = new byte[1 << 20];
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
            sha.TransformBlock(buffer, 0, read, null, 0);
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }
}

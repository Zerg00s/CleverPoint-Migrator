using System.Security.Cryptography;
using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Version history copy: a file with 3 distinct content versions migrates
/// with MaxVersions=3; the target must hold 3 versions whose contents match
/// the source versions exactly (per-version SHA-256).
/// </summary>
public static class VersionTests
{
    private const string LibTitle = "MigTest-VerLib";
    private const string FileName = "versioned.bin";

    public static async Task RunAsync()
    {
        var site = Program.TestSite ?? await TestAssets.EnsureTestSiteAsync(Program.Source);
        Program.TestSite = site;

        // ---- Provision: library with a 3-version file ----
        using var ctx = site.CreateContext();
        var deleted = await TestAssets.DeleteIfExistsAsync(ctx, LibTitle);
        List lib;
        if (deleted)
        {
            lib = ctx.Web.Lists.Add(new ListCreationInformation
            {
                Title = LibTitle,
                TemplateType = (int)ListTemplateType.DocumentLibrary,
                Url = "MigTestVerLib",
            });
            lib.EnableVersioning = true;
            lib.Update();
            await ctx.ExecuteQueryAsync();
        }
        else
        {
            lib = ctx.Web.Lists.GetByTitle(LibTitle);
        }
        ctx.Load(lib.RootFolder, f => f.ServerRelativeUrl);
        await ctx.ExecuteQueryAsync();
        var root = lib.RootFolder.ServerRelativeUrl;

        var rng = new Random(2024);
        var hashes = new List<string>();
        for (var v = 0; v < 3; v++)
        {
            var bytes = new byte[2048 + v * 1024];
            rng.NextBytes(bytes);
            hashes.Add(Convert.ToHexString(SHA256.HashData(bytes)));
            ctx.Web.GetFolderByServerRelativePath(ResourcePath.FromDecodedUrl(root)).Files.Add(new FileCreationInformation
            {
                Url = FileName,
                ContentStream = new MemoryStream(bytes),
                Overwrite = true,
            });
            await ctx.ExecuteQueryAsync();
        }
        Console.WriteLine($"  provisioned {FileName} with 3 versions");

        // ---- Copy with version history ----
        await TestAssets.DeleteIfExistsAsync(ctx, "MigTest-VerCopy");
        var result = await CopyEngine.CopyListAsync(site, site, LibTitle, new CopyOptions
        {
            TargetListTitle = "MigTest-VerCopy",
            TargetListUrl = "MigTestVerCopy",
            MaxVersions = 3,
        });
        Program.Check("versions: copy no failures", result.Failed == 0, result.Summary());
        Program.Check("versions: version trail reported",
            result.Records.Any(r => r.ItemType == "File" && (r.Message?.Contains("3 versions") ?? false)));

        // ---- Verify: target has 3 versions, contents exact per version ----
        using var vctx = site.CreateContext();
        var tgtLib = vctx.Web.Lists.GetByTitle("MigTest-VerCopy");
        vctx.Load(tgtLib.RootFolder, f => f.ServerRelativeUrl);
        vctx.Load(vctx.Web, w => w.Url);
        await vctx.ExecuteQueryAsync();
        var tgtRef = $"{tgtLib.RootFolder.ServerRelativeUrl}/{FileName}";
        var tgtWebUrl = vctx.Web.Url.TrimEnd('/');

        string EscapePath(string s) => Uri.EscapeDataString(s.Replace("'", "''"));
        using var versionsDoc = await Program.Source.Rest.GetJsonAsync(
            $"{tgtWebUrl}/_api/web/GetFileByServerRelativePath(decodedUrl='{EscapePath(tgtRef)}')/versions?$select=ID&$orderby=ID asc");
        var olderVersions = versionsDoc.RootElement.GetProperty("value").EnumerateArray().ToList();
        Program.Check("versions: target has 2 older versions + current", olderVersions.Count == 2, $"{olderVersions.Count} older");

        var actualHashes = new List<string>();
        foreach (var version in olderVersions)
        {
            var bytes = await Program.Source.Rest.GetBytesAsync(
                $"{tgtWebUrl}/_api/web/GetFileByServerRelativePath(decodedUrl='{EscapePath(tgtRef)}')/versions({version.GetProperty("ID").GetInt32()})/$value");
            actualHashes.Add(Convert.ToHexString(SHA256.HashData(bytes)));
        }
        var currentBytes = await Program.Source.Rest.GetBytesAsync(
            $"{tgtWebUrl}/_api/web/GetFileByServerRelativePath(decodedUrl='{EscapePath(tgtRef)}')/$value");
        actualHashes.Add(Convert.ToHexString(SHA256.HashData(currentBytes)));

        Program.Check("versions: all 3 version contents match source exactly",
            actualHashes.SequenceEqual(hashes),
            string.Join(" / ", actualHashes.Zip(hashes, (a, e) => a == e ? "ok" : "DIFF")));
    }
}

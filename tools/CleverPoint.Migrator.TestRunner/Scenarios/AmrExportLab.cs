using System.Text;
using System.Text.Json;
using CleverPoint.Migrator.Core.MigrationApi;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Lab: ask SharePoint itself to export the source library as a migration
/// package (AMR / CreateSPAsyncReadJob), download the produced manifest
/// blobs, and save them locally. The exported Manifest.xml is the ground
/// truth for what the import deserializer expects; our package builder is
/// aligned to it.
/// </summary>
public static class AmrExportLab
{
    public static async Task RunAsync()
    {
        var site = Program.TestSite ?? await TestAssets.EnsureTestSiteAsync(Program.Source);
        Program.TestSite = site;
        var azure = new AzureStorageRestClient();

        using var ctx = site.CreateContext();
        var list = ctx.Web.Lists.GetByTitle(TestAssets.SourceLibTitle);
        ctx.Load(list.RootFolder, f => f.ServerRelativeUrl);
        ctx.Load(ctx.Web, w => w.Url);
        await ctx.ExecuteQueryAsync();

        var containers = ctx.Site.ProvisionMigrationContainers();
        var queue = ctx.Site.ProvisionMigrationQueue();
        await ctx.ExecuteQueryAsync();
        var key = containers.Value.EncryptionKey;
        var manifestContainer = containers.Value.MetadataContainerUri;
        var queueUri = queue.Value.JobQueueUri;

        var libraryUrl = new Uri(new Uri(ctx.Web.Url), list.RootFolder.ServerRelativeUrl).ToString();
        Console.WriteLine($"  exporting: {libraryUrl}");

        var job = ctx.Site.CreateSPAsyncReadJob(
            libraryUrl,
            new AsyncReadOptions { IncludeDirectDescendantsOnly = false, IncludeSecurity = false },
            new EncryptionOption { AES256CBCKey = key },
            manifestContainer,
            queueUri);
        await ctx.ExecuteQueryAsync();
        Console.WriteLine($"  AMR job: {job.Value.JobId}");

        // Collect queue events; remember every blob name SharePoint reports.
        var blobNames = new List<string>();
        var finished = false;
        var deadline = DateTime.UtcNow.AddMinutes(10);
        while (!finished && DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(8));
            foreach (var (text, _, _) in await azure.GetQueueMessagesAsync(queueUri))
            {
                var json = Decrypt(text, key);
                Console.WriteLine($"  queue: {(json.Length > 220 ? json[..220] + "..." : json)}");
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("FileName", out var f) && f.GetString() is { } name)
                        blobNames.Add(name);
                    if (doc.RootElement.TryGetProperty("ManifestFileName", out var mf) && mf.GetString() is { } mname)
                        blobNames.Add(mname);
                    if (doc.RootElement.TryGetProperty("Event", out var e) && e.GetString() == "JobEnd")
                        finished = true;
                }
                catch { /* keep going */ }
            }
        }

        var outDir = Path.Combine(Directory.GetCurrentDirectory(), "amr-output");
        Directory.CreateDirectory(outDir);
        var saved = 0;

        // Try reported names plus well-known manifest names.
        var candidates = blobNames.Concat(new[]
        {
            "Manifest.xml", "Manifest-1.xml", "ExportSettings.xml", "SystemData.xml",
            "UserGroup.xml", "RootObjectMap.xml", "LookupListMap.xml", "Requirements.xml", "ViewFormsList.xml",
        }).Distinct().ToList();

        foreach (var name in candidates)
        {
            string? iv = null;
            var bytes = await azure.DownloadBlobAsync(manifestContainer, name, (k, v) => { if (k.Equals("IV", StringComparison.OrdinalIgnoreCase)) iv = v; });
            if (bytes == null) continue;
            if (iv != null)
            {
                try { bytes = Aes256Cbc.Decrypt(bytes, key, iv); }
                catch (Exception ex) { Console.WriteLine($"  decrypt failed for {name}: {ex.Message}"); continue; }
            }
            var path = Path.Combine(outDir, name.Replace('/', '_'));
            await System.IO.File.WriteAllBytesAsync(path, bytes);
            Console.WriteLine($"  saved: {path} ({bytes.Length} bytes)");
            saved++;
        }

        Program.Check("amr export: manifest blobs saved", saved > 0, $"{saved} files in {outDir}");

        // Show the interesting part: how SharePoint represents a file item.
        var manifestPath = Directory.GetFiles(outDir).FirstOrDefault(p => Path.GetFileName(p).StartsWith("Manifest"));
        if (manifestPath != null)
        {
            var xml = await System.IO.File.ReadAllTextAsync(manifestPath, Encoding.UTF8);
            var idx = xml.IndexOf("SPListItem", StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
                Console.WriteLine(xml[Math.Max(0, idx - 200)..Math.Min(xml.Length, idx + 1800)]);
        }
    }

    private static string Decrypt(string text, byte[] key)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("Label", out var label) && label.GetString() == "Encrypted")
                return Encoding.UTF8.GetString(Aes256Cbc.Decrypt(
                    Convert.FromBase64String(doc.RootElement.GetProperty("Content").GetString()!),
                    key, doc.RootElement.GetProperty("IV").GetString()!));
        }
        catch { }
        return text;
    }
}

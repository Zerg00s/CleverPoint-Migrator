using System.Text;
using System.Xml.Linq;

namespace CleverPoint.Migrator.Core.MigrationApi;

/// <summary>
/// Minimal Azure Blob/Queue REST client working purely off SAS URIs (the form
/// returned by ProvisionMigrationContainers / ProvisionMigrationQueue). No
/// Azure SDK dependency.
/// </summary>
public class AzureStorageRestClient
{
    private const string ApiVersion = "2020-10-02";
    private readonly HttpClient _http;

    public AzureStorageRestClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    /// <summary>Combines a container SAS URI with a blob name: base/blob?sas[&amp;extra].</summary>
    public static string BlobUrl(string containerSasUri, string blobName, string? extraQuery = null)
    {
        var qIndex = containerSasUri.IndexOf('?');
        var basePart = qIndex < 0 ? containerSasUri : containerSasUri[..qIndex];
        var sas = qIndex < 0 ? "" : containerSasUri[(qIndex + 1)..];
        var query = extraQuery == null ? sas : $"{extraQuery}&{sas}";
        // Blob names may contain '/' path segments; keep them as separators.
        var escaped = Uri.EscapeDataString(blobName).Replace("%2F", "/");
        return $"{basePart.TrimEnd('/')}/{escaped}?{query}";
    }

    /// <summary>Uploads a block blob with the Migration API "IV" metadata and creates the required snapshot.</summary>
    public async Task UploadBlobWithSnapshotAsync(string containerSasUri, string blobName, byte[] content, string ivBase64)
    {
        using (var put = new HttpRequestMessage(HttpMethod.Put, BlobUrl(containerSasUri, blobName)))
        {
            put.Headers.Add("x-ms-blob-type", "BlockBlob");
            put.Headers.Add("x-ms-version", ApiVersion);
            put.Headers.Add("x-ms-meta-IV", ivBase64);
            put.Content = new ByteArrayContent(content);
            var response = await _http.SendAsync(put);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Blob upload failed ({(int)response.StatusCode}) for '{blobName}': {await response.Content.ReadAsStringAsync()}");
        }

        // Migration API only imports blobs that have at least one snapshot.
        using var snap = new HttpRequestMessage(HttpMethod.Put, BlobUrl(containerSasUri, blobName, "comp=snapshot"));
        snap.Headers.Add("x-ms-version", ApiVersion);
        var snapResponse = await _http.SendAsync(snap);
        if (!snapResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Blob snapshot failed ({(int)snapResponse.StatusCode}) for '{blobName}': {await snapResponse.Content.ReadAsStringAsync()}");
    }

    public async Task<byte[]?> DownloadBlobAsync(string containerSasUri, string blobName, Action<string, string>? metadataSink = null)
    {
        using var get = new HttpRequestMessage(HttpMethod.Get, BlobUrl(containerSasUri, blobName));
        get.Headers.Add("x-ms-version", ApiVersion);
        var response = await _http.SendAsync(get);
        if (!response.IsSuccessStatusCode) return null;
        foreach (var header in response.Headers.Where(h => h.Key.StartsWith("x-ms-meta-", StringComparison.OrdinalIgnoreCase)))
            metadataSink?.Invoke(header.Key["x-ms-meta-".Length..], string.Join(",", header.Value));
        return await response.Content.ReadAsByteArrayAsync();
    }

    /// <summary>Dequeues up to 32 messages (text, messageId, popReceipt).</summary>
    public async Task<List<(string Text, string MessageId, string PopReceipt)>> GetQueueMessagesAsync(string queueSasUri)
    {
        // queueSasUri is ".../queuename?sas"; messages endpoint is ".../queuename/messages?numofmessages=32&sas".
        var qIndex = queueSasUri.IndexOf('?');
        var basePart = queueSasUri[..qIndex].TrimEnd('/');
        var sas = queueSasUri[(qIndex + 1)..];
        var url = $"{basePart}/messages?numofmessages=32&{sas}";

        using var get = new HttpRequestMessage(HttpMethod.Get, url);
        get.Headers.Add("x-ms-version", ApiVersion);
        var response = await _http.SendAsync(get);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Queue read failed ({(int)response.StatusCode}): {body}");

        var results = new List<(string, string, string)>();
        var doc = XDocument.Parse(body);
        foreach (var msg in doc.Descendants("QueueMessage"))
        {
            var text = msg.Element("MessageText")?.Value ?? "";
            // Azure queue text from SPO is base64-encoded JSON; fall back to raw.
            try { text = Encoding.UTF8.GetString(Convert.FromBase64String(text)); }
            catch (FormatException) { }
            results.Add((text, msg.Element("MessageId")?.Value ?? "", msg.Element("PopReceipt")?.Value ?? ""));
        }
        return results;
    }

    /// <summary>
    /// Deletes one processed queue message. Without this, SPO's GET only hides a message
    /// for its visibility timeout (~30 s), so an error event is re-read every poll into
    /// duplicate log rows, or is lost in a race with the job-status fallback.
    /// </summary>
    public async Task DeleteQueueMessageAsync(string queueSasUri, string messageId, string popReceipt)
    {
        if (string.IsNullOrEmpty(messageId) || string.IsNullOrEmpty(popReceipt)) return;
        var qIndex = queueSasUri.IndexOf('?');
        var basePart = queueSasUri[..qIndex].TrimEnd('/');
        var sas = queueSasUri[(qIndex + 1)..];
        var url = $"{basePart}/messages/{messageId}?popreceipt={Uri.EscapeDataString(popReceipt)}&{sas}";
        using var del = new HttpRequestMessage(HttpMethod.Delete, url);
        del.Headers.Add("x-ms-version", ApiVersion);
        await _http.SendAsync(del);   // best-effort; a 404 (already gone) is harmless
    }
}

using System.Text;
using CleverPoint.Migrator.Core.Csom;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.Core.Validation;

/// <summary>
/// The user-facing "Compare" feature: runs a source-vs-target comparison for
/// any list pair (typically after a migration) and produces an accounting
/// report with totals and per-item detail, exportable as CSV or a
/// styled standalone HTML page with clickable item links.
/// </summary>
public class CompareReport
{
    public string SourceList { get; set; } = "";
    public string TargetList { get; set; } = "";
    public string? SourceSiteUrl { get; set; }
    public string? TargetSiteUrl { get; set; }
    public DateTime RanUtc { get; set; } = DateTime.UtcNow;
    public int SourceItems { get; set; }
    public int TargetItems { get; set; }
    public List<string> Mismatches { get; } = new();

    public bool IsClean => Mismatches.Count == 0;

    /// <summary>Whether file contents were compared at all, and (if so) the 1-in-N sample ratio.</summary>
    public bool ContentCompared { get; set; }
    public int ContentSampleEvery { get; set; } = 1;

    // Measured evidence for the client deliverable.
    public int ItemsPaired { get; set; }
    public int FilesHashVerified { get; set; }
    public long BytesHashVerified { get; set; }
    public int SourceFiles { get; set; }
    public long StorageBytes { get; set; }            // target library storage (incl. versions), 0 = unknown

    // Optional branding for a hand-off document.
    public string? ClientName { get; set; }
    public string? PreparedBy { get; set; }
    public string ToolVersion { get; set; } = "";

    // Disclose HOW thorough the check was, so a clean metadata compare is not read as a
    // byte-for-byte verification (a truncated blob with matching metadata would pass).
    public string Verdict
    {
        get
        {
            if (!IsClean) return Mismatches.Count <= 5 ? "Minor differences" : "Differences found";
            if (!ContentCompared) return "Metadata accounted for (file contents not compared)";
            if (ContentSampleEvery > 1) return $"Accounted for (file contents sampled 1-in-{ContentSampleEvery})";
            return "Fully accounted for (contents verified)";
        }
    }

    public static async Task<CompareReport> RunAsync(SpConnection source, SpConnection target,
        string sourceListTitle, string targetListTitle, IEnumerable<string> compareFields,
        bool compareContent = false, int contentSampleEvery = 10,
        Dictionary<int, int>? itemMap = null, bool compareUsers = true)
    {
        using var sourceCtx = source.CreateContext();
        using var targetCtx = target.CreateContext();
        var sourceList = sourceCtx.Web.Lists.GetByTitle(sourceListTitle);
        var targetList = targetCtx.Web.Lists.GetByTitle(targetListTitle);
        sourceCtx.Load(sourceList, l => l.ItemCount);
        targetCtx.Load(targetList, l => l.ItemCount);
        await sourceCtx.ExecuteQueryAsync();
        await targetCtx.ExecuteQueryAsync();

        var report = new CompareReport
        {
            SourceList = sourceListTitle,
            TargetList = targetListTitle,
            SourceSiteUrl = source.SiteUrl,
            TargetSiteUrl = target.SiteUrl,
            SourceItems = sourceList.ItemCount,
            TargetItems = targetList.ItemCount,
            ContentCompared = compareContent,
            ContentSampleEvery = compareContent ? Math.Max(1, contentSampleEvery) : 1,
        };

        var stats = new CopyVerifier.VerificationStats();
        var verifier = new CopyVerifier(sourceCtx, targetCtx) { ItemMap = itemMap };
        report.Mismatches.AddRange(await verifier.VerifyAsync(sourceList, targetList, compareFields,
            compareFileContent: compareContent, compareUsers: compareUsers,
            contentSampleEvery: contentSampleEvery, stats: stats));
        report.ItemsPaired = stats.ItemsPaired;
        report.FilesHashVerified = stats.FilesHashChecked;
        report.BytesHashVerified = stats.BytesHashChecked;
        report.SourceFiles = stats.SourceFiles;

        // Target library storage (one cheap call; includes version overhead, labelled as such).
        try
        {
            var root = targetList.RootFolder;
            targetCtx.Load(root, f => f.StorageMetrics.TotalSize);
            await targetCtx.ExecuteQueryAsync();
            report.StorageBytes = root.StorageMetrics.TotalSize;
        }
        catch { /* generic lists have no storage metrics; leave 0 */ }

        return report;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes; var i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return v >= 100 || i == 0 ? $"{v:0} {u[i]}" : $"{v:0.0} {u[i]}";
    }

    /// <summary>SHA-256 coverage phrase for the report, honest about sampling.</summary>
    private string HashCoverage()
    {
        if (!ContentCompared) return "Not performed (metadata-only compare)";
        if (SourceFiles == 0) return "No files to hash";
        if (ContentSampleEvery > 1)
            return $"{FilesHashVerified:N0} of {SourceFiles:N0} files (1-in-{ContentSampleEvery} sample), {FormatBytes(BytesHashVerified)} hashed";
        return $"{FilesHashVerified:N0} of {SourceFiles:N0} files (every file), {FormatBytes(BytesHashVerified)} hashed";
    }

    public void ExportCsv(string path)
    {
        var sb = new StringBuilder("Detail\n");
        foreach (var m in Mismatches)
        {
            // Neutralize spreadsheet formula injection (leading = + - @ / tab / CR).
            var v = m.Length > 0 && m[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + m : m;
            sb.AppendLine("\"" + v.Replace("\"", "\"\"") + "\"");
        }
        System.IO.File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    public void ExportHtml(string path)
    {
        static string H(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");

        // Badge: green when clean, amber for a handful of differences, red beyond that.
        var accent = IsClean ? "#1e7e34" : Mismatches.Count <= 5 ? "#b8860b" : "#b02a37";
        var badge = IsClean ? "VERIFIED" : Mismatches.Count <= 5 ? "MINOR DIFFERENCES" : "DIFFERENCES FOUND";

        var detail = Mismatches.Count == 0
            ? "<tr><td class=\"ok\">Every source item was found on the target and all compared attributes matched.</td></tr>"
            : string.Join("\n", Mismatches.Select(m => $"<tr><td>{H(m)}</td></tr>"));

        var storageRow = StorageBytes > 0
            ? $"<div class=\"stat\"><span class=\"n\">{FormatBytes(StorageBytes)}</span><span class=\"l\">Target storage</span></div>"
            : "";

        var subtitle = string.IsNullOrWhiteSpace(ClientName)
            ? "Migration verification report"
            : $"Migration verification report for {H(ClientName)}";
        var preparedBy = string.IsNullOrWhiteSpace(PreparedBy) ? "" :
            $"<tr><th>Prepared by</th><td>{H(PreparedBy)}</td></tr>";
        var version = string.IsNullOrWhiteSpace(ToolVersion) ? "" : $" v{H(ToolVersion)}";

        // Methodology, stated plainly so the verdict cannot be over-read.
        var method = ContentCompared
            ? (ContentSampleEvery > 1
                ? $"Item counts, metadata (fields, Created/Modified, Author/Editor) and file contents were compared. File contents were verified with SHA-256 hashes on a 1-in-{ContentSampleEvery} sample."
                : "Item counts, metadata (fields, Created/Modified, Author/Editor) and file contents were compared. Every file's content was verified with a SHA-256 hash of source vs target.")
            : "Item counts and metadata (fields, Created/Modified, Author/Editor) were compared. File contents were not hashed in this pass.";

        var html = $$"""
            <!DOCTYPE html><html lang="en"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{H(subtitle)}}</title>
            <style>
              :root { --accent: {{accent}}; }
              * { box-sizing: border-box; }
              body { font-family: 'Segoe UI', system-ui, sans-serif; margin: 0; color: #1b1f24; background: #f6f8fa; }
              .sheet { max-width: 900px; margin: 0 auto; background: #fff; }
              header { padding: 32px 40px; border-top: 6px solid var(--accent); border-bottom: 1px solid #e6e8eb; display: flex; align-items: center; justify-content: space-between; gap: 20px; flex-wrap: wrap; }
              header h1 { margin: 0 0 4px; font-size: 1.5em; }
              header .sub { color: #5a6169; font-size: .95em; }
              .badge { flex: 0 0 auto; font-weight: 700; letter-spacing: .04em; color: #fff; background: var(--accent); padding: 8px 16px; border-radius: 999px; font-size: .85em; white-space: nowrap; }
              main { padding: 28px 40px 40px; }
              .stats { display: flex; gap: 14px; flex-wrap: wrap; margin: 0 0 26px; }
              .stat { flex: 1 1 130px; background: #f6f8fa; border: 1px solid #e6e8eb; border-radius: 10px; padding: 14px 16px; }
              .stat .n { display: block; font-size: 1.5em; font-weight: 700; font-variant-numeric: tabular-nums; }
              .stat .l { display: block; color: #5a6169; font-size: .82em; margin-top: 2px; }
              h2 { font-size: 1.05em; margin: 26px 0 10px; }
              table { border-collapse: collapse; width: 100%; font-size: .92em; }
              td, th { border: 1px solid #e6e8eb; padding: 8px 12px; text-align: left; vertical-align: top; }
              th { background: #f6f8fa; width: 190px; color: #414852; font-weight: 600; }
              .detail-table td { font-family: ui-monospace, 'Cascadia Code', Consolas, monospace; font-size: .86em; word-break: break-word; }
              .detail-table td.ok { font-family: 'Segoe UI', sans-serif; color: #1e7e34; }
              .method { background: #f6f8fa; border-left: 4px solid var(--accent); padding: 12px 16px; border-radius: 0 8px 8px 0; color: #414852; font-size: .92em; }
              footer { padding: 18px 40px 30px; color: #8a9099; font-size: .82em; border-top: 1px solid #e6e8eb; }
            </style></head><body>
            <div class="sheet">
              <header>
                <div>
                  <h1>{{H(subtitle)}}</h1>
                  <div class="sub">{{H(SourceList)}} &rarr; {{H(TargetList)}}</div>
                </div>
                <div class="badge">{{badge}}</div>
              </header>
              <main>
                <div class="stats">
                  <div class="stat"><span class="n">{{SourceItems:N0}}</span><span class="l">Source items</span></div>
                  <div class="stat"><span class="n">{{TargetItems:N0}}</span><span class="l">Target items</span></div>
                  <div class="stat"><span class="n">{{ItemsPaired:N0}}</span><span class="l">Items matched</span></div>
                  <div class="stat"><span class="n">{{Mismatches.Count:N0}}</span><span class="l">Differences</span></div>
                  {{storageRow}}
                </div>

                <h2>Verdict</h2>
                <p style="font-size:1.15em;font-weight:600;color:var(--accent);margin:0 0 18px;">{{H(Verdict)}}</p>

                <h2>Evidence</h2>
                <table>
                  <tr><th>Source</th><td>{{H(SourceList)}}{{(SourceSiteUrl is null ? "" : $" &middot; {H(SourceSiteUrl)}")}}</td></tr>
                  <tr><th>Target</th><td>{{H(TargetList)}}{{(TargetSiteUrl is null ? "" : $" &middot; {H(TargetSiteUrl)}")}}</td></tr>
                  <tr><th>SHA-256 coverage</th><td>{{H(HashCoverage())}}</td></tr>
                  <tr><th>Verified (UTC)</th><td>{{RanUtc:yyyy-MM-dd HH:mm}} UTC</td></tr>
                  {{preparedBy}}
                </table>

                <h2>How this was verified</h2>
                <p class="method">{{method}}</p>

                <h2>Findings{{(Mismatches.Count > 0 ? $" ({Mismatches.Count})" : "")}}</h2>
                <table class="detail-table">{{detail}}</table>
              </main>
              <footer>Generated by CleverPoint Migrator{{version}} on {{RanUtc:yyyy-MM-dd}} &middot; created by Denis Molodtsov</footer>
            </div>
            </body></html>
            """;
        System.IO.File.WriteAllText(path, html, Encoding.UTF8);
    }
}

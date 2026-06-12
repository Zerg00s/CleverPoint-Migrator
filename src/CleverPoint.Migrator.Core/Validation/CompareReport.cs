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
    public DateTime RanUtc { get; set; } = DateTime.UtcNow;
    public int SourceItems { get; set; }
    public int TargetItems { get; set; }
    public List<string> Mismatches { get; } = new();

    public bool IsClean => Mismatches.Count == 0;
    public string Verdict => IsClean ? "Fully accounted for"
        : Mismatches.Count <= 5 ? "Minor differences" : "Differences found";

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
            SourceItems = sourceList.ItemCount,
            TargetItems = targetList.ItemCount,
        };

        var verifier = new CopyVerifier(sourceCtx, targetCtx) { ItemMap = itemMap };
        report.Mismatches.AddRange(await verifier.VerifyAsync(sourceList, targetList, compareFields,
            compareFileContent: compareContent, compareUsers: compareUsers, contentSampleEvery: contentSampleEvery));
        return report;
    }

    public void ExportCsv(string path)
    {
        var sb = new StringBuilder("Detail\n");
        foreach (var m in Mismatches)
            sb.AppendLine("\"" + m.Replace("\"", "\"\"") + "\"");
        System.IO.File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    public void ExportHtml(string path)
    {
        var color = IsClean ? "#1e7e34" : Mismatches.Count <= 5 ? "#b8860b" : "#b02a37";
        var rows = Mismatches.Count == 0
            ? "<tr><td>Everything matched. All items are accounted for.</td></tr>"
            : string.Join("\n", Mismatches.Select(m => $"<tr><td>{System.Net.WebUtility.HtmlEncode(m)}</td></tr>"));
        var html = $$"""
            <!DOCTYPE html><html><head><meta charset="utf-8"><title>Migration compare report</title>
            <style>
              body { font-family: 'Segoe UI', sans-serif; margin: 40px; color: #212529; }
              .verdict { font-size: 1.4em; color: {{color}}; font-weight: 600; }
              table { border-collapse: collapse; margin-top: 16px; width: 100%; }
              td, th { border: 1px solid #dee2e6; padding: 6px 10px; text-align: left; }
              th { background: #f1f3f5; }
            </style></head><body>
            <h1>Migration compare report</h1>
            <p class="verdict">{{Verdict}}</p>
            <table>
              <tr><th>Source</th><td>{{System.Net.WebUtility.HtmlEncode(SourceList)}} ({{SourceItems}} items)</td></tr>
              <tr><th>Target</th><td>{{System.Net.WebUtility.HtmlEncode(TargetList)}} ({{TargetItems}} items)</td></tr>
              <tr><th>Compared</th><td>{{RanUtc:yyyy-MM-dd HH:mm}} UTC</td></tr>
              <tr><th>Differences</th><td>{{Mismatches.Count}}</td></tr>
            </table>
            <h2>Detail</h2>
            <table>{{rows}}</table>
            <p style="color:#868e96">CleverPoint Migrator - created by Denis Molodtsov</p>
            </body></html>
            """;
        System.IO.File.WriteAllText(path, html, Encoding.UTF8);
    }
}

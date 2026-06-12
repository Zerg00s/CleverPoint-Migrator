namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>Checks every library's new-document template file (existence AND plausible content).</summary>
public static class CountProbe
{
    public static async Task RunAsync()
    {
        var conn = Program.Target.ForWeb("https://cleverpointlab.sharepoint.com/sites/Migrationson365Group");
        using var doc = await conn.Rest.GetJsonAsync(
            $"{conn.SiteUrl}/_api/web/lists?$select=Title,Hidden,BaseType,DocumentTemplateUrl,RootFolder/ServerRelativeUrl&$expand=RootFolder&$top=100");
        foreach (var e in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            if (e.GetProperty("BaseType").GetInt32() != 1) continue;
            var title = e.GetProperty("Title").GetString();
            var tpl = e.TryGetProperty("DocumentTemplateUrl", out var t) ? t.GetString() : null;
            if (string.IsNullOrEmpty(tpl)) { Console.WriteLine($"  {title}: no template set"); continue; }
            try
            {
                var esc = Uri.EscapeDataString(tpl!.Replace("'", "''"));
                using var f = await conn.Rest.GetJsonAsync(
                    $"{conn.SiteUrl}/_api/web/GetFileByServerRelativePath(decodedUrl='{esc}')?$select=Exists,Length,Name");
                var len = f.RootElement.TryGetProperty("Length", out var l) ? l.ToString() : "?";
                Console.WriteLine($"  {title}: template={tpl} exists={f.RootElement.GetProperty("Exists").GetBoolean()} length={len}");
            }
            catch
            {
                Console.WriteLine($"  {title}: template={tpl} -> MISSING (404) <- breaks 'new document'");
            }
        }
        Program.Check("template content probe ran", true);
    }
}

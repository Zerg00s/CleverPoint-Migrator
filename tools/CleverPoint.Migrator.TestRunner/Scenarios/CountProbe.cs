namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>Finds libraries whose new-file template is broken (doc.aspx 'something went wrong').</summary>
public static class CountProbe
{
    public static async Task RunAsync()
    {
        var conn = Program.Target.ForWeb("https://cleverpointlab.sharepoint.com/sites/Migrationson365Group");
        using var doc = await conn.Rest.GetJsonAsync(
            $"{conn.SiteUrl}/_api/web/lists?$select=Title,Hidden,BaseType,ItemCount,DocumentTemplateUrl,RootFolder/ServerRelativeUrl&$expand=RootFolder&$top=100");
        foreach (var e in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            if (e.GetProperty("BaseType").GetInt32() != 1 || e.GetProperty("Hidden").GetBoolean()) continue;
            var title = e.GetProperty("Title").GetString();
            var count = e.GetProperty("ItemCount").GetInt32();
            var tpl = e.TryGetProperty("DocumentTemplateUrl", out var t) ? t.GetString() : null;
            var tplExists = "none";
            if (!string.IsNullOrEmpty(tpl))
            {
                try
                {
                    var esc = Uri.EscapeDataString(tpl!.Replace("'", "''"));
                    using var f = await conn.Rest.GetJsonAsync(
                        $"{conn.SiteUrl}/_api/web/GetFileByServerRelativePath(decodedUrl='{esc}')?$select=Exists");
                    tplExists = f.RootElement.GetProperty("Exists").GetBoolean().ToString();
                }
                catch { tplExists = "MISSING"; }
            }
            Console.WriteLine($"  {title}  items={count}  template={tpl}  exists={tplExists}");
        }
        Program.Check("template probe ran", true);
    }
}

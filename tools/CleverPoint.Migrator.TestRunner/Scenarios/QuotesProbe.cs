namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>Diagnostic: dump BaseType/BaseTemplate of the QUOTES list on the
/// LMAS source site and the Demo-AppsMigrator target site, to explain the
/// wizard's "library vs list" mismatch chip.</summary>
public static class QuotesProbe
{
    public static async Task RunAsync()
    {
        await DumpAsync("SOURCE LMAS", Program.Source.ForWeb("https://gocleverpointcom.sharepoint.com/sites/LMAS"));
        await DumpAsync("TARGET Demo-AppsMigrator", Program.Source.ForWeb("https://gocleverpointcom.sharepoint.com/sites/Demo-AppsMigrator"));
        Program.Check("quotes probe ran", true);
    }

    private static async Task DumpAsync(string label, Core.Csom.SpConnection conn)
    {
        Console.WriteLine($"  --- {label} ({conn.SiteUrl}) ---");
        try
        {
            using var doc = await conn.Rest.GetJsonAsync(
                $"{conn.SiteUrl}/_api/web/lists?$select=Title,Hidden,BaseType,BaseTemplate,ItemCount,RootFolder/ServerRelativeUrl&$expand=RootFolder&$filter=Title eq 'QUOTES'&$top=10");
            var arr = doc.RootElement.GetProperty("value");
            if (arr.GetArrayLength() == 0) { Console.WriteLine("    no list titled 'QUOTES'"); return; }
            foreach (var e in arr.EnumerateArray())
            {
                Console.WriteLine($"    Title={e.GetProperty("Title").GetString()} " +
                    $"BaseType={e.GetProperty("BaseType").GetInt32()} " +
                    $"BaseTemplate={e.GetProperty("BaseTemplate").GetInt32()} " +
                    $"Hidden={e.GetProperty("Hidden").GetBoolean()} " +
                    $"ItemCount={e.GetProperty("ItemCount").GetInt32()} " +
                    $"Url={e.GetProperty("RootFolder").GetProperty("ServerRelativeUrl").GetString()}");
            }
        }
        catch (Exception ex) { Console.WriteLine($"    ERROR: {ex.Message}"); }
    }
}

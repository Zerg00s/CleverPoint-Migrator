namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Proves the identity mapper's people/group fetch (F-Map): the same REST endpoints and JSON
/// shape SiteBrowser.GetPrincipalsAsync reads. Lives in the TestRunner (Core-only) since the
/// Photino Ux project can't be referenced here; it's a faithful proxy for that method.
/// </summary>
public static class PrincipalFetchTest
{
    public static async Task RunAsync()
    {
        var site = Program.TestSite ?? await TestAssets.EnsureTestSiteAsync(Program.Source);
        Program.TestSite = site;

        var users = 0; var groups = 0; string? sampleUser = null;
        using (var doc = await site.Rest.GetJsonAsync(
            $"{site.SiteUrl}/_api/web/siteusers?$select=LoginName,Title,Email,PrincipalType&$top=500"))
        {
            foreach (var e in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                var login = e.GetProperty("LoginName").GetString() ?? "";
                if (login.Length == 0 || login.Contains("app@sharepoint", StringComparison.OrdinalIgnoreCase)) continue;
                var pt = e.TryGetProperty("PrincipalType", out var p) ? p.GetInt32() : 1;
                if (pt == 1) { users++; sampleUser ??= e.GetProperty("Title").GetString(); } else groups++;
            }
        }
        using (var doc = await site.Rest.GetJsonAsync(
            $"{site.SiteUrl}/_api/web/sitegroups?$select=LoginName,Title&$top=200"))
        {
            groups += doc.RootElement.GetProperty("value").EnumerateArray().Count();
        }

        Console.WriteLine($"  principals: {users} users, {groups} groups (sample user '{sampleUser}')");
        Program.Check("principals: site users fetched + parsed", users > 0, $"{users} users");
        Program.Check("principals: site groups fetched + parsed", groups > 0, $"{groups} groups");
    }
}

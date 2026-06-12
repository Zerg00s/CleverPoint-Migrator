using System.Text.Json;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Checks whether the signed-in admin's account on the TARGET tenant has an
/// Office-for-the-web license: creating NEW Office documents in the browser
/// requires one, and a missing license produces exactly the doc.aspx
/// "Sorry, something went wrong" on every library. Uses Graph (app mgmt /
/// directory read only - no SharePoint data).
/// </summary>
public static class LicenseProbe
{
    public static async Task RunAsync()
    {
        var token = await new CleverPoint.Migrator.Core.Auth.CertTokenProvider(
            CleverPoint.Migrator.Core.Auth.AppCredentials.LoadFromFile(
                Path.Combine(FindRoot(), "Secrets", "Target Tenant - Migrator App.txt")))
            .GetTokenAsync("graph.microsoft.com");
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var users = await http.GetStringAsync(
            "https://graph.microsoft.com/v1.0/users?$select=displayName,userPrincipalName,id&$top=25");
        using var doc = JsonDocument.Parse(users);
        foreach (var u in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            var upn = u.GetProperty("userPrincipalName").GetString()!;
            var id = u.GetProperty("id").GetString()!;
            var lic = await http.GetStringAsync($"https://graph.microsoft.com/v1.0/users/{id}/licenseDetails");
            using var licDoc = JsonDocument.Parse(lic);
            var skus = licDoc.RootElement.GetProperty("value").EnumerateArray()
                .Select(l => l.GetProperty("skuPartNumber").GetString()).ToList();
            var officeWeb = licDoc.RootElement.GetProperty("value").EnumerateArray()
                .SelectMany(l => l.GetProperty("servicePlans").EnumerateArray())
                .Any(p => (p.GetProperty("servicePlanName").GetString() ?? "").Contains("SHAREPOINTWAC")
                    && p.GetProperty("provisioningStatus").GetString() == "Success");
            Console.WriteLine($"  {upn}: licenses=[{string.Join(",", skus)}] officeForWeb={officeWeb}");
        }
        Program.Check("license probe ran", true);
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Secrets"))) dir = dir.Parent;
        return dir!.FullName;
    }
}

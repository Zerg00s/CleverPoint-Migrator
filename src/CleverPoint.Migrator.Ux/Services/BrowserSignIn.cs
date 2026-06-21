using System.Diagnostics;
using System.Text.Json;

namespace CleverPoint.Migrator.Ux.Services;

/// <summary>
/// Browser sign-in for the Photino app. Photino can't read the WebView's
/// HttpOnly FedAuth cookie, so on Windows this launches the WebView2 sign-in
/// helper exe, captures FedAuth + rtFa, and caches the session per host so it
/// is REUSED across every site collection and migration on the same tenant.
/// (Browser sign-in is Windows-only; Linux/WSL uses app + certificate.)
/// </summary>
public class BrowserSignIn
{
    private static readonly Dictionary<string, (string FedAuth, string RtFa)> Sessions = new(StringComparer.OrdinalIgnoreCase);

    public bool Available => OperatingSystem.IsWindows();

    public (string FedAuth, string RtFa)? GetSession(string siteUrl)
        => Sessions.TryGetValue(Host(siteUrl), out var s) ? s : null;

    public bool HasSession(string siteUrl) => Sessions.ContainsKey(Host(siteUrl));

    public void Clear(string siteUrl) => Sessions.Remove(Host(siteUrl));

    public async Task<(bool Ok, string Message)> SignInAsync(string siteUrl, bool fresh = false)
    {
        if (!OperatingSystem.IsWindows())
            return (false, "Browser sign-in needs Windows (WebView2). On Linux/WSL use an app + certificate connection.");

        var helper = LocateHelper();
        if (helper is null)
            return (false, "Sign-in helper not found. Build CleverPoint.Migrator.SignInHelper.");

        var outFile = Path.Combine(Path.GetTempPath(), $"cpm-signin-{Guid.NewGuid():N}.json");
        try
        {
            var psi = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = false };
            psi.ArgumentList.Add(siteUrl);
            psi.ArgumentList.Add(outFile);
            if (fresh) psi.ArgumentList.Add("--fresh");
            var proc = Process.Start(psi);
            if (proc is null) return (false, "Could not start the sign-in helper.");
            await proc.WaitForExitAsync();

            if (proc.ExitCode == 0 && File.Exists(outFile))
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(outFile));
                var fed = doc.RootElement.GetProperty("fedAuth").GetString() ?? "";
                var rt = doc.RootElement.TryGetProperty("rtFa", out var r) ? r.GetString() ?? "" : "";
                Sessions[Host(siteUrl)] = (fed, rt);
                return (true, "Signed in.");
            }
            return (false, "Sign-in was cancelled or did not complete.");
        }
        catch (Exception ex)
        {
            return (false, $"Sign-in failed: {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(outFile)) File.Delete(outFile); } catch { }
        }
    }

    private static string Host(string siteUrl) => new Uri(siteUrl.TrimEnd('/')).Host;

    private static string? LocateHelper()
    {
        const string exe = "CleverPoint.Migrator.SignInHelper.exe";
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, exe),
            Path.Combine(AppContext.BaseDirectory, "SignInHelper", exe),
        };
        // Dev fallback: the helper's build output relative to the running assembly.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var src = Path.Combine(dir.FullName, "src", "CleverPoint.Migrator.SignInHelper", "bin");
            if (Directory.Exists(src))
            {
                var found = Directory.GetFiles(src, exe, SearchOption.AllDirectories).FirstOrDefault();
                if (found is not null) candidates.Add(found);
            }
        }
        return candidates.FirstOrDefault(File.Exists);
    }
}

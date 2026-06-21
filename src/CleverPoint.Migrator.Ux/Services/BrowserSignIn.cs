using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
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
    private sealed record Session(string FedAuth, string RtFa, DateTime CapturedUtc);

    private static readonly Dictionary<string, Session> Sessions = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;
    // Captured FedAuth cookies are reusable for a while; cap it so a very stale one
    // doesn't get reused into confusing 401s. SPO persistent cookies usually outlive this.
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(18);
    private static string StorePath => Path.Combine(UxSettings.Folder, "sessions.dat");

    public bool Available => OperatingSystem.IsWindows();

    public (string FedAuth, string RtFa)? GetSession(string siteUrl)
    {
        EnsureLoaded();
        if (Sessions.TryGetValue(Host(siteUrl), out var s))
        {
            if (DateTime.UtcNow - s.CapturedUtc < MaxAge) return (s.FedAuth, s.RtFa);
            Sessions.Remove(Host(siteUrl));
            Save();
        }
        return null;
    }

    public bool HasSession(string siteUrl) => GetSession(siteUrl) is not null;

    public void Clear(string siteUrl) { EnsureLoaded(); Sessions.Remove(Host(siteUrl)); Save(); }

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
                EnsureLoaded();
                Sessions[Host(siteUrl)] = new Session(fed, rt, DateTime.UtcNow);
                Save();
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

    // Persist captured sessions (DPAPI-encrypted, per Windows user) so they survive
    // app restarts and are reused across source/target and every site on the host.
    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(StorePath)) return;
            var bytes = File.ReadAllBytes(StorePath);
            var json = OperatingSystem.IsWindows()
                ? Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser))
                : Encoding.UTF8.GetString(bytes);
            var stored = JsonSerializer.Deserialize<Dictionary<string, Session>>(json);
            if (stored is null) return;
            Sessions.Clear();
            foreach (var kv in stored)
                if (DateTime.UtcNow - kv.Value.CapturedUtc < MaxAge) Sessions[kv.Key] = kv.Value;
        }
        catch { /* corrupt/unreadable store: start fresh */ }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(UxSettings.Folder);
            var json = JsonSerializer.Serialize(Sessions);
            var bytes = OperatingSystem.IsWindows()
                ? ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser)
                : Encoding.UTF8.GetBytes(json);
            File.WriteAllBytes(StorePath, bytes);
        }
        catch { /* best-effort persistence */ }
    }

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

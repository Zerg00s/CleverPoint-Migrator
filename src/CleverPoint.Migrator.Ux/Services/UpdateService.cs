using System.Text.Json;
using CleverPoint.Migrator.Core.Updates;

namespace CleverPoint.Migrator.Ux.Services;

/// <summary>The outcome of an update check.</summary>
public record UpdateInfo(bool Available, string CurrentVersion, string? LatestVersion,
    string? Notes, string? ReleaseUrl, string? MsiUrl)
{
    public static UpdateInfo UpToDate(string current) => new(false, current, null, null, null, null);
}

/// <summary>
/// Checks the public GitHub Releases API for a newer build (no auth, no Graph, no SharePoint).
/// Auto-runs at most once per day on launch; the Settings page can force a check any time.
/// </summary>
public class UpdateService
{
    private const string LatestReleaseApi = "https://api.github.com/repos/Zerg00s/CleverPoint-Migrator/releases/latest";
    private static readonly HttpClient Http = CreateClient();

    private readonly UxSettings _settings;
    public UpdateService(UxSettings settings) => _settings = settings;

    public static string CurrentVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // GitHub requires a User-Agent; the API version header keeps the shape stable.
        c.DefaultRequestHeaders.UserAgent.ParseAdd("CleverPoint-Migrator");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>Whether a daily auto-check is due (more than 24h since the last one).</summary>
    public bool IsAutoCheckDue() =>
        _settings.LastUpdateCheckUtc is not { } last || (DateTime.UtcNow - last) > TimeSpan.FromHours(24);

    /// <summary>
    /// Queries the latest release and compares versions. Records the check time (so the daily
    /// gate resets) unless this is a forced check. Never throws: on any error returns up-to-date.
    /// </summary>
    public async Task<UpdateInfo> CheckAsync(bool recordTime = true)
    {
        var current = CurrentVersion;
        try
        {
            using var resp = await Http.GetAsync(LatestReleaseApi);
            if (recordTime) { _settings.LastUpdateCheckUtc = DateTime.UtcNow; _settings.Save(); }
            if (!resp.IsSuccessStatusCode) return UpdateInfo.UpToDate(current);

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (!UpdateVersion.IsNewer(current, tag)) return UpdateInfo.UpToDate(current);

            var notes = root.TryGetProperty("body", out var b) ? b.GetString() : null;
            var url = root.TryGetProperty("html_url", out var h) ? h.GetString() : null;
            string? msi = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var a in assets.EnumerateArray())
                    if (a.TryGetProperty("name", out var n) && (n.GetString() ?? "").EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                        msi = a.TryGetProperty("browser_download_url", out var d) ? d.GetString() : null;

            return new UpdateInfo(true, current, UpdateVersion.Parse(tag)?.ToString() ?? tag, notes, url, msi);
        }
        catch
        {
            return UpdateInfo.UpToDate(current);
        }
    }

    /// <summary>
    /// Downloads the release MSI to a temp file and launches it (per-user install: no UAC),
    /// then signals the app to exit so the installer can replace files. Returns false if there
    /// is no MSI asset (the caller should open the release page instead).
    /// </summary>
    public async Task<bool> DownloadAndRunInstallerAsync(UpdateInfo info)
    {
        if (string.IsNullOrEmpty(info.MsiUrl)) return false;
        var path = Path.Combine(Path.GetTempPath(), $"CleverPointMigrator-{info.LatestVersion}.msi");
        await using (var src = await Http.GetStreamAsync(info.MsiUrl))
        await using (var dst = File.Create(path))
            await src.CopyToAsync(dst);
        // Per-user MSI: msiexec launches without elevation and upgrades in place.
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("msiexec", $"/i \"{path}\"") { UseShellExecute = true });
        return true;
    }
}

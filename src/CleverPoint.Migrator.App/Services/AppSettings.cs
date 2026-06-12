using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CleverPoint.Migrator.App.Services;

/// <summary>One remembered tenant connection. Secrets ride DPAPI-encrypted.</summary>
public class SavedConnection
{
    public string Name { get; set; } = "";
    public string SiteUrl { get; set; } = "";
    public string AuthMode { get; set; } = "Browser";   // Browser | AppCertificate
    public string TenantId { get; set; } = "";
    public string AppId { get; set; } = "";
    public string CertPfxPath { get; set; } = "";
    public string CertPasswordProtected { get; set; } = "";   // DPAPI, base64

    /// <summary>Last health-check result ("Connected", "Failed: ...", "Signs in on use").</summary>
    public string LastStatus { get; set; } = "";
    public DateTime? LastVerifiedUtc { get; set; }

    /// <summary>Certificate expiry (app+certificate mode); browser sessions are per-use.</summary>
    public DateTime? CertExpiresUtc { get; set; }
}

/// <summary>
/// Global app settings, persisted as JSON under %LocalAppData%.
/// Migration-level options live in CopyOptions presets, not here.
/// </summary>
public class AppSettings
{
    public List<SavedConnection> Connections { get; set; } = new();
    public bool ShowCompletionToasts { get; set; } = true;
    public bool MinimizeToTray { get; set; }
    public bool StartWithWindows { get; set; }
    public int MaxParallelMigrations { get; set; } = 2;
    public double MaxRequestsPerSecond { get; set; } = 0;     // 0 = unlimited
    public int CacheMinutes { get; set; } = 15;

    /// <summary>Self-healing: auto re-run incrementals (up to 5) after a run with failures. Off by default.</summary>
    public bool SelfHealAutoRetry { get; set; }

    /// <summary>Self-healing: detect corrupt target files (0-byte / truncated), delete OUR copies and re-copy. Off by default.</summary>
    public bool SelfHealRepairCorrupt { get; set; }

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CleverPointMigrator");

    public static string HistoryDbPath => Path.Combine(Folder, "history.db");
    public static string CacheFolder => Path.Combine(Folder, "cache");
    private static string SettingsPath => Path.Combine(Folder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[CPMigrator] settings load failed: {ex.Message}");
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void ClearCache()
    {
        if (Directory.Exists(CacheFolder))
            Directory.Delete(CacheFolder, true);
    }

    public static string Protect(string secret) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser));

    public static string Unprotect(string protectedValue) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), null, DataProtectionScope.CurrentUser));
}

using System.Text.Json;

namespace CleverPoint.Migrator.Ux.Services;

/// <summary>A saved tenant connection. Mirrors the WinForms app's shape so the
/// same settings.json works in both front-ends.</summary>
public class SavedConnection
{
    public string Name { get; set; } = "";
    public string SiteUrl { get; set; } = "";
    public string AuthMode { get; set; } = "Browser";   // Browser | AppCertificate
    public string TenantId { get; set; } = "";
    public string AppId { get; set; } = "";
    public string CertPfxPath { get; set; } = "";
    public string CertPasswordProtected { get; set; } = "";
    public string LastStatus { get; set; } = "";
    public DateTime? LastVerifiedUtc { get; set; }
    public DateTime? CertExpiresUtc { get; set; }
}

/// <summary>
/// Cross-platform app settings backed by the SAME settings.json the WinForms
/// app uses (so connections, toggles and history are shared on Windows). On
/// Linux/WSL it falls back to a local config path so the UI is testable.
/// </summary>
public class UxSettings
{
    public List<SavedConnection> Connections { get; set; } = new();
    public bool ShowCompletionToasts { get; set; } = true;
    public int MaxParallelMigrations { get; set; } = 2;
    public int CacheMinutes { get; set; } = 15;
    public bool SelfHealAutoRetry { get; set; }
    public bool SelfHealRepairCorrupt { get; set; }
    /// <summary>How many automatic incremental retry passes after a run with failures (1-5).</summary>
    public int SelfHealMaxAttempts { get; set; } = 5;
    /// <summary>Default engine for new migrations: "Classic" or "MigrationApi".</summary>
    public string DefaultEngine { get; set; } = "Classic";
    /// <summary>Default file-version depth for new migrations (1, 5, 10, 50).</summary>
    public int DefaultMaxVersions { get; set; } = 1;
    /// <summary>"Light" or "Dark"; persisted so the theme survives restarts even if WebView localStorage is wiped.</summary>
    public string Theme { get; set; } = "Light";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Folder
    {
        get
        {
            // Windows: %AppData%\CleverPoint Migrator (same as the WinForms app).
            // Linux/WSL: ~/.config/CleverPoint Migrator.
            var baseDir = OperatingSystem.IsWindows()
                ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(baseDir, "CleverPoint Migrator");
        }
    }

    public static string SettingsPath => Path.Combine(Folder, "settings.json");
    public static string HistoryDbPath => Path.Combine(Folder, "history.db");
    public static string CacheFolder => Path.Combine(Folder, "cache");

    // Loaded snapshot reused by the singleton; reloaded on demand.
    private bool _loaded;

    public void EnsureLoaded()
    {
        if (_loaded) return;
        Reload();
        _loaded = true;
    }

    public void Reload()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<UxSettings>(File.ReadAllText(SettingsPath));
                if (loaded != null)
                {
                    Connections = loaded.Connections;
                    ShowCompletionToasts = loaded.ShowCompletionToasts;
                    MaxParallelMigrations = loaded.MaxParallelMigrations;
                    CacheMinutes = loaded.CacheMinutes;
                    SelfHealAutoRetry = loaded.SelfHealAutoRetry;
                    SelfHealRepairCorrupt = loaded.SelfHealRepairCorrupt;
                    SelfHealMaxAttempts = loaded.SelfHealMaxAttempts is >= 1 and <= 5 ? loaded.SelfHealMaxAttempts : 5;
                    DefaultEngine = string.IsNullOrWhiteSpace(loaded.DefaultEngine) ? "Classic" : loaded.DefaultEngine;
                    DefaultMaxVersions = loaded.DefaultMaxVersions <= 0 ? 1 : loaded.DefaultMaxVersions;
                    Theme = string.IsNullOrWhiteSpace(loaded.Theme) ? "Light" : loaded.Theme;
                }
            }
        }
        catch { /* first run / unreadable: keep defaults */ }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, Json));
        }
        catch { /* best-effort */ }
    }
}

using System.Text.Json;
using CleverPoint.Migrator.Core.Operations;

namespace CleverPoint.Migrator.Ux.Services;

/// <summary>
/// One saved source->target identity mapping: user logins/emails and group names remapped
/// to their target-tenant equivalents, plus an optional fallback for unresolved (orphaned)
/// users. Applied to a run through CopyEngine's userMap/groupMap and
/// CopyOptions.UnresolvedUserFallback.
/// </summary>
public class IdentityMapping
{
    public Dictionary<string, string> Users { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Groups { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Target login every UNMAPPED/unresolvable source user falls back to. Null = leave
    /// unresolved (the engine warns). The built-in System Account is a first-class choice.</summary>
    public string? OrphanFallbackLogin { get; set; }
    public string? OrphanFallbackLabel { get; set; }

    public bool IsEmpty => Users.Count == 0 && Groups.Count == 0 && string.IsNullOrEmpty(OrphanFallbackLogin);

    /// <summary>Re-key the dictionaries case-insensitively after a JSON round-trip (the
    /// serializer drops the comparer).</summary>
    public IdentityMapping Normalized()
    {
        Users = new Dictionary<string, string>(Users, StringComparer.OrdinalIgnoreCase);
        Groups = new Dictionary<string, string>(Groups, StringComparer.OrdinalIgnoreCase);
        return this;
    }
}

/// <summary>
/// Persists identity mappings per source-tenant -> target-tenant pair (keyed by host, so one
/// mapping is reused across every list migration between those two tenants). Round-trips CSV
/// via the engine's UserMappingStore.
/// </summary>
public class UxMappingStore
{
    /// <summary>The built-in SharePoint System Account, offered as an orphan-user target.</summary>
    public const string SystemAccountLogin = "SHAREPOINT\\system";
    public const string SystemAccountLabel = "System Account";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static string Dir => Path.Combine(UxSettings.Folder, "mappings");

    private static string HostKey(string sourceSite, string targetSite)
    {
        static string Host(string u) { try { return new Uri(u).Host; } catch { return u; } }
        var raw = $"{Host(sourceSite)}__{Host(targetSite)}";
        return string.Concat(raw.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));
    }

    public static string PathFor(string sourceSite, string targetSite)
        => Path.Combine(Dir, HostKey(sourceSite, targetSite) + ".json");

    public IdentityMapping Load(string sourceSite, string targetSite)
    {
        try
        {
            var path = PathFor(sourceSite, targetSite);
            if (File.Exists(path))
                return (JsonSerializer.Deserialize<IdentityMapping>(File.ReadAllText(path)) ?? new()).Normalized();
        }
        catch { /* fall through to an empty mapping */ }
        return new IdentityMapping();
    }

    public void Save(string sourceSite, string targetSite, IdentityMapping mapping)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(PathFor(sourceSite, targetSite), JsonSerializer.Serialize(mapping, Json));
    }

    /// <summary>Import mappings from a Type,Source,Target CSV (engine format), merged in.</summary>
    public void ImportCsv(IdentityMapping into, string csvPath)
    {
        var (users, groups) = UserMappingStore.LoadCsv(csvPath);
        foreach (var kv in users) into.Users[kv.Key] = kv.Value;
        foreach (var kv in groups) into.Groups[kv.Key] = kv.Value;
    }

    /// <summary>Export the current mapping to the engine's Type,Source,Target CSV format.</summary>
    public void ExportCsv(IdentityMapping mapping, string csvPath)
    {
        var rows = mapping.Users.Select(kv => ("User", kv.Key, kv.Value))
            .Concat(mapping.Groups.Select(kv => ("Group", kv.Key, kv.Value)));
        UserMappingStore.SaveCsv(csvPath, rows);
    }
}

using System.Text.Json;

namespace CleverPoint.Migrator.Core.Model;

/// <summary>
/// Named migration-settings presets ("templates"), stored and exchanged as
/// JSON. The UI surfaces these as a subtle save/load affordance next to the
/// settings, not a separate management screen.
/// </summary>
public static class SettingsPresets
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static void Export(CopyOptions options, string name, string path)
    {
        var doc = new PresetFile { Name = name, SavedUtc = DateTime.UtcNow, Options = options };
        File.WriteAllText(path, JsonSerializer.Serialize(doc, Json));
    }

    public static (string Name, CopyOptions Options) Import(string path)
    {
        var doc = JsonSerializer.Deserialize<PresetFile>(File.ReadAllText(path), Json)
            ?? throw new InvalidOperationException($"'{path}' is not a valid settings preset.");
        // Runtime-only state never belongs in a preset.
        doc.Options.ResumeSkipPaths = null;
        doc.Options.UpsertItemMap = null;
        doc.Options.TargetListTitle = doc.Options.TargetListTitle;  // titles/urls travel; caller may override
        return (doc.Name, doc.Options);
    }

    private class PresetFile
    {
        public string Name { get; set; } = "";
        public DateTime SavedUtc { get; set; }
        public CopyOptions Options { get; set; } = new();
    }
}

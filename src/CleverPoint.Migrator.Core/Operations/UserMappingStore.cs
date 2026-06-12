using System.Text;

namespace CleverPoint.Migrator.Core.Operations;

/// <summary>
/// Identity mapping as a user-editable CSV: rows of
/// "Type,Source,Target" where Type is User or Group, Source is a UPN /
/// claims login / email (users) or a principal name (groups), and Target is
/// the equivalent on the target tenant. No Entra access needed: the file is
/// produced/consumed locally and applied through the resolver.
/// </summary>
public static class UserMappingStore
{
    public static (Dictionary<string, string> Users, Dictionary<string, string> Groups) LoadCsv(string path)
    {
        var users = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var groups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(path).Skip(1))
        {
            var cells = ParseCsvLine(line);
            if (cells.Count < 3 || string.IsNullOrWhiteSpace(cells[1]) || string.IsNullOrWhiteSpace(cells[2])) continue;
            if (cells[0].Equals("Group", StringComparison.OrdinalIgnoreCase))
                groups[cells[1].Trim()] = cells[2].Trim();
            else
                users[cells[1].Trim()] = cells[2].Trim();
        }
        return (users, groups);
    }

    public static void SaveCsv(string path, IEnumerable<(string Type, string Source, string Target)> rows)
    {
        var sb = new StringBuilder("Type,Source,Target\n");
        static string Q(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        foreach (var (type, source, target) in rows)
            sb.AppendLine($"{Q(type)},{Q(source)},{Q(target)}");
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    /// <summary>
    /// Exports a mapping template: every source site user/group, with Target
    /// prefilled by auto-detection where the target site resolved it and
    /// blank where the admin must decide. Round-trips through LoadCsv.
    /// </summary>
    public static void ExportTemplate(string path,
        IEnumerable<(string Login, string Email, string Title)> sourceUsers,
        IEnumerable<string> sourceGroups,
        IReadOnlyDictionary<string, string> autoDetected)
    {
        var rows = new List<(string, string, string)>();
        foreach (var u in sourceUsers)
        {
            var key = string.IsNullOrEmpty(u.Email) ? u.Login : u.Email;
            rows.Add(("User", key, autoDetected.GetValueOrDefault(key, "")));
        }
        foreach (var g in sourceGroups)
            rows.Add(("Group", g, autoDetected.GetValueOrDefault(g, "")));
        SaveCsv(path, rows);
    }

    private static List<string> ParseCsvLine(string line)
    {
        var cells = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') inQuotes = false;
                else sb.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { cells.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        cells.Add(sb.ToString());
        return cells;
    }
}

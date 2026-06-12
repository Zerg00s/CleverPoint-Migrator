using System.Text;
using CleverPoint.Migrator.Core.Model;
using Microsoft.Data.Sqlite;

namespace CleverPoint.Migrator.Core.History;

public class MigrationRun
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string SourceList { get; set; } = "";
    public string TargetUrl { get; set; } = "";
    public string TargetList { get; set; } = "";
    public string Engine { get; set; } = "Classic";   // Classic | MigrationApi
    public DateTime StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public string Status { get; set; } = "Running";   // Running | Completed | CompletedWithIssues | Failed | Interrupted
    public int Copied { get; set; }
    public int Skipped { get; set; }
    public int Warnings { get; set; }
    public int Failed { get; set; }

    /// <summary>Max server-stamped Modified seen on the source during the run; the next delta's baseline.</summary>
    public DateTime? MaxSourceModifiedUtc { get; set; }
}

/// <summary>
/// SQLite-backed migration history: runs and their per-item log rows.
/// Powers the history screen, delta re-runs, resume of interrupted runs,
/// and full log export. Comfortable at the few-thousand-runs scale.
/// </summary>
public class HistoryStore : IDisposable
{
    private readonly SqliteConnection _db;

    public HistoryStore(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        _db = new SqliteConnection($"Data Source={dbPath}");
        _db.Open();
        Exec("""
            CREATE TABLE IF NOT EXISTS runs(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT, source_url TEXT, source_list TEXT,
                target_url TEXT, target_list TEXT, engine TEXT,
                started_utc TEXT, finished_utc TEXT, status TEXT,
                copied INT DEFAULT 0, skipped INT DEFAULT 0,
                warnings INT DEFAULT 0, failed INT DEFAULT 0);
            CREATE TABLE IF NOT EXISTS run_items(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id INT NOT NULL, item_type TEXT, source_path TEXT,
                target_path TEXT, status TEXT, message TEXT,
                size_bytes INT, duration_ms INT, item_url TEXT);
            CREATE INDEX IF NOT EXISTS ix_items_run ON run_items(run_id, status);
            CREATE INDEX IF NOT EXISTS ix_items_path ON run_items(run_id, source_path);
            CREATE TABLE IF NOT EXISTS item_map(
                pair TEXT NOT NULL, source_id INT NOT NULL, target_id INT NOT NULL,
                PRIMARY KEY(pair, source_id));
            """);
        // Schema upgrades for databases created by earlier builds.
        try { Exec("ALTER TABLE runs ADD COLUMN max_modified_utc TEXT"); }
        catch (SqliteException) { /* column already there */ }
    }

    public long StartRun(MigrationRun run)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO runs(name, source_url, source_list, target_url, target_list, engine, started_utc, status)
            VALUES($n, $su, $sl, $tu, $tl, $e, $st, 'Running');
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$n", run.Name);
        cmd.Parameters.AddWithValue("$su", run.SourceUrl);
        cmd.Parameters.AddWithValue("$sl", run.SourceList);
        cmd.Parameters.AddWithValue("$tu", run.TargetUrl);
        cmd.Parameters.AddWithValue("$tl", run.TargetList);
        cmd.Parameters.AddWithValue("$e", run.Engine);
        cmd.Parameters.AddWithValue("$st", DateTime.UtcNow.ToString("o"));
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>Persists one log row. The item URL makes every file clickable in the UI/log.</summary>
    public void RecordItem(long runId, ItemCopyRecord rec, string? itemUrl = null)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO run_items(run_id, item_type, source_path, target_path, status, message, size_bytes, duration_ms, item_url)
            VALUES($r, $t, $sp, $tp, $s, $m, $b, $d, $u)
            """;
        cmd.Parameters.AddWithValue("$r", runId);
        cmd.Parameters.AddWithValue("$t", rec.ItemType);
        cmd.Parameters.AddWithValue("$sp", rec.SourcePath);
        cmd.Parameters.AddWithValue("$tp", rec.TargetPath);
        cmd.Parameters.AddWithValue("$s", rec.Status.ToString());
        cmd.Parameters.AddWithValue("$m", (object?)rec.Message ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$b", rec.SizeBytes);
        cmd.Parameters.AddWithValue("$d", (long)rec.Duration.TotalMilliseconds);
        cmd.Parameters.AddWithValue("$u", (object?)itemUrl ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void FinishRun(long runId, CopyResult result, string status)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            UPDATE runs SET finished_utc=$f, status=$s, copied=$c, skipped=$k, warnings=$w, failed=$x,
                max_modified_utc=COALESCE($m, max_modified_utc) WHERE id=$id
            """;
        cmd.Parameters.AddWithValue("$m", (object?)result.MaxSourceModifiedUtc?.ToString("o") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$f", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$s", status);
        cmd.Parameters.AddWithValue("$c", result.Copied);
        cmd.Parameters.AddWithValue("$k", result.Skipped);
        cmd.Parameters.AddWithValue("$w", result.Warnings);
        cmd.Parameters.AddWithValue("$x", result.Failed);
        cmd.Parameters.AddWithValue("$id", runId);
        cmd.ExecuteNonQuery();
    }

    public List<MigrationRun> GetRuns(int limit = 200)
    {
        var runs = new List<MigrationRun>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT id,name,source_url,source_list,target_url,target_list,engine,started_utc,finished_utc,status,copied,skipped,warnings,failed,max_modified_utc FROM runs ORDER BY id DESC LIMIT $l";
        cmd.Parameters.AddWithValue("$l", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            runs.Add(new MigrationRun
            {
                Id = r.GetInt64(0), Name = r.GetString(1), SourceUrl = r.GetString(2), SourceList = r.GetString(3),
                TargetUrl = r.GetString(4), TargetList = r.GetString(5), Engine = r.GetString(6),
                StartedUtc = DateTime.Parse(r.GetString(7)).ToUniversalTime(),
                FinishedUtc = r.IsDBNull(8) ? null : DateTime.Parse(r.GetString(8)).ToUniversalTime(),
                Status = r.GetString(9), Copied = r.GetInt32(10), Skipped = r.GetInt32(11),
                Warnings = r.GetInt32(12), Failed = r.GetInt32(13),
                MaxSourceModifiedUtc = r.IsDBNull(14) ? null : DateTime.Parse(r.GetString(14)).ToUniversalTime(),
            });
        }
        return runs;
    }

    /// <summary>The most recent run for a source/target pair (delta baseline + resume detection).</summary>
    public MigrationRun? GetLastRun(string sourceUrl, string sourceList, string targetUrl, string targetList) =>
        GetRuns(1000).FirstOrDefault(r =>
            r.SourceUrl.Equals(sourceUrl, StringComparison.OrdinalIgnoreCase)
            && r.SourceList.Equals(sourceList, StringComparison.OrdinalIgnoreCase)
            && r.TargetUrl.Equals(targetUrl, StringComparison.OrdinalIgnoreCase)
            && r.TargetList.Equals(targetList, StringComparison.OrdinalIgnoreCase));

    /// <summary>Source paths already copied in a run (resume support).</summary>
    public HashSet<string> GetCopiedSourcePaths(long runId)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT source_path FROM run_items WHERE run_id=$r AND status='Copied'";
        cmd.Parameters.AddWithValue("$r", runId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) paths.Add(r.GetString(0));
        return paths;
    }

    public List<(string ItemType, string SourcePath, string TargetPath, string Status, string? Message, string? ItemUrl)> GetItems(long runId, string? status = null)
    {
        var items = new List<(string, string, string, string, string?, string?)>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT item_type,source_path,target_path,status,message,item_url FROM run_items WHERE run_id=$r"
            + (status != null ? " AND status=$s" : "");
        cmd.Parameters.AddWithValue("$r", runId);
        if (status != null) cmd.Parameters.AddWithValue("$s", status);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            items.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)));
        return items;
    }

    /// <summary>Full log export as CSV (Excel-friendly, quoted).</summary>
    public void ExportRunCsv(long runId, string path)
    {
        var sb = new StringBuilder("ItemType,SourcePath,TargetPath,Status,Message,SizeBytes,DurationMs,ItemUrl\n");
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT item_type,source_path,target_path,status,message,size_bytes,duration_ms,item_url FROM run_items WHERE run_id=$r";
        cmd.Parameters.AddWithValue("$r", runId);
        using var r = cmd.ExecuteReader();
        static string Q(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        while (r.Read())
            sb.AppendLine(string.Join(',', Q(r.GetString(0)), Q(r.GetString(1)), Q(r.GetString(2)), Q(r.GetString(3)),
                Q(r.IsDBNull(4) ? "" : r.GetString(4)), r.GetInt64(5), r.GetInt64(6), Q(r.IsDBNull(7) ? "" : r.GetString(7))));
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    /// <summary>Key identifying a source->target list pair for the item map.</summary>
    public static string PairKey(string sourceUrl, string sourceList, string targetUrl, string targetList) =>
        $"{sourceUrl}|{sourceList}=>{targetUrl}|{targetList}".ToLowerInvariant();

    /// <summary>Persists source item id -> target item id mappings (delta upserts depend on these).</summary>
    public void SaveItemMap(string pair, IEnumerable<(int SourceId, int TargetId)> mappings)
    {
        using var tx = _db.BeginTransaction();
        foreach (var (s, t) in mappings)
        {
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR REPLACE INTO item_map(pair, source_id, target_id) VALUES($p, $s, $t)";
            cmd.Parameters.AddWithValue("$p", pair);
            cmd.Parameters.AddWithValue("$s", s);
            cmd.Parameters.AddWithValue("$t", t);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public Dictionary<int, int> GetItemMap(string pair)
    {
        var map = new Dictionary<int, int>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT source_id, target_id FROM item_map WHERE pair=$p";
        cmd.Parameters.AddWithValue("$p", pair);
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetInt32(0)] = r.GetInt32(1);
        return map;
    }

    /// <summary>Runs are user-nameable and renamable at any time (including after completion).</summary>
    public void RenameRun(long runId, string newName)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "UPDATE runs SET name=$n WHERE id=$id";
        cmd.Parameters.AddWithValue("$n", newName);
        cmd.Parameters.AddWithValue("$id", runId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Settings: "Clear migration history". Item maps survive unless wipeItemMaps (they power delta upserts).</summary>
    public void ClearHistory(bool wipeItemMaps = false)
    {
        Exec("DELETE FROM run_items; DELETE FROM runs;" + (wipeItemMaps ? " DELETE FROM item_map;" : ""));
        Exec("VACUUM");
    }

    private void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _db.Dispose();
}

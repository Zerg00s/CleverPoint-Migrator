using Microsoft.Data.Sqlite;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Dev utility: fills the Fluent UI app's history database with 1000 realistic
/// mock runs (varied statuses, engines, sites, lists, dates and counts) plus a
/// few log rows each, so the Run History screen can be exercised at scale.
/// Writes to the same path the app reads: %AppData%\CleverPoint Migrator\history.db
/// on Windows. Run it on Windows so the path resolves to the app's database.
/// </summary>
public static class SeedHistoryTests
{
    private const int RunCount = 1000;

    private static readonly string[] Sites =
    {
        "https://gocleverpointcom.sharepoint.com/sites/LMAS",
        "https://gocleverpointcom.sharepoint.com/sites/DemoLargeSite",
        "https://gocleverpointcom.sharepoint.com/sites/HR",
        "https://gocleverpointcom.sharepoint.com/sites/Finance",
        "https://gocleverpointcom.sharepoint.com/sites/Projects",
    };
    private static readonly string[] Targets =
    {
        "https://cleverpointlab.sharepoint.com/sites/Migrationson365Group",
        "https://cleverpointlab.sharepoint.com/sites/Archive",
        "https://cleverpointlab.sharepoint.com",
    };
    private static readonly string[] Lists =
    {
        "Documents", "Shared Documents", "25000 HBTA (Archive)", "Large Group", "Site Pages",
        "Quotes", "2007 Profiles", "PCL Constructors 19668 84999", "North Majors - Archive",
        "Contracts", "Invoices", "Policies", "Meeting Notes", "Photos", "Templates",
    };
    private static readonly (string Status, int Weight)[] Statuses =
    {
        ("Completed", 68), ("CompletedWithIssues", 16), ("Failed", 8), ("Interrupted", 6), ("Running", 2),
    };

    public static Task RunAsync()
    {
        // Default to the app's real DB path (resolves correctly when run on Windows).
        // CP_HISTORY_DB lets us point at it explicitly (e.g. seeding the Windows file
        // from WSL: the Windows %AppData% maps to /mnt/c/Users/<you>/AppData/Roaming).
        var dbPath = Environment.GetEnvironmentVariable("CP_HISTORY_DB");
        if (string.IsNullOrWhiteSpace(dbPath))
            dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CleverPoint Migrator", "history.db");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        Console.WriteLine($"  seeding {RunCount} mock runs into {dbPath}");

        using var db = new SqliteConnection($"Data Source={dbPath}");
        db.Open();
        EnsureSchema(db);

        var rng = new Random(20260621);
        var nowUtc = DateTime.UtcNow;
        var added = 0;

        using (var tx = db.BeginTransaction())
        {
            for (var i = 0; i < RunCount; i++)
            {
                var status = WeightedStatus(rng);
                var engine = rng.Next(100) < 35 ? "MigrationApi" : "Classic";
                var source = Sites[rng.Next(Sites.Length)];
                var target = Targets[rng.Next(Targets.Length)];
                var list = Lists[rng.Next(Lists.Length)];
                var targetList = list;

                // Spread starts across the last ~120 days, most recent last (higher id).
                var minutesAgo = (long)((RunCount - i) * 170 + rng.Next(0, 160));
                var started = nowUtc.AddMinutes(-minutesAgo);
                var durationMin = rng.Next(1, 240);
                DateTime? finished = status == "Running" ? null : started.AddMinutes(durationMin);

                // Item counts scaled to make a believable spread (some huge libraries).
                var copied = status == "Failed" ? rng.Next(0, 40) : rng.Next(5, 15000);
                var skipped = rng.Next(0, copied / 8 + 1);
                var warnings = status is "CompletedWithIssues" or "Failed" ? rng.Next(1, 60) : rng.Next(0, 3);
                var failed = status switch
                {
                    "Failed" => rng.Next(5, 200),
                    "CompletedWithIssues" => rng.Next(1, 25),
                    "Interrupted" => rng.Next(0, 10),
                    _ => 0,
                };

                var runId = InsertRun(db, tx, $"{list} -> {targetList}", source, list, target, targetList,
                    engine, started, finished, status, copied, skipped, warnings, failed);

                // A handful of log rows per run so the detail view has content.
                var sampleRows = rng.Next(3, 9);
                for (var k = 0; k < sampleRows; k++)
                {
                    var (it, st) = SampleItem(rng, status);
                    InsertItem(db, tx, runId, it,
                        $"{source}/{list}/sample-{k}.docx", $"{target}/{targetList}/sample-{k}.docx",
                        st, st == "Failed" ? "copy error: throttled" : null,
                        started.AddMinutes(rng.Next(0, Math.Max(1, durationMin))));
                }
                added++;
            }
            tx.Commit();
        }

        Program.Check("seed-history: mock runs written", added == RunCount, $"{added} runs at {dbPath}");
        Console.WriteLine("  done. Open the app's Run History screen to see them.");
        return Task.CompletedTask;
    }

    private static string WeightedStatus(Random rng)
    {
        var total = Statuses.Sum(s => s.Weight);
        var roll = rng.Next(total);
        var acc = 0;
        foreach (var (status, weight) in Statuses)
        {
            acc += weight;
            if (roll < acc) return status;
        }
        return "Completed";
    }

    private static (string ItemType, string Status) SampleItem(Random rng, string runStatus)
    {
        var type = rng.Next(3) switch { 0 => "Folder", 1 => "File", _ => "Item" };
        var status = runStatus == "Failed" && rng.Next(2) == 0 ? "Failed"
            : rng.Next(10) switch { 0 => "Warning", 1 => "Skipped", _ => "Copied" };
        return (type, status);
    }

    private static void EnsureSchema(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS runs(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT, source_url TEXT, source_list TEXT,
                target_url TEXT, target_list TEXT, engine TEXT,
                started_utc TEXT, finished_utc TEXT, status TEXT,
                copied INT DEFAULT 0, skipped INT DEFAULT 0,
                warnings INT DEFAULT 0, failed INT DEFAULT 0,
                max_modified_utc TEXT, scope_json TEXT);
            CREATE TABLE IF NOT EXISTS run_items(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id INT NOT NULL, item_type TEXT, source_path TEXT,
                target_path TEXT, status TEXT, message TEXT,
                size_bytes INT, duration_ms INT, item_url TEXT, ts TEXT);
            """;
        cmd.ExecuteNonQuery();
    }

    private static long InsertRun(SqliteConnection db, SqliteTransaction tx, string name,
        string sourceUrl, string sourceList, string targetUrl, string targetList, string engine,
        DateTime started, DateTime? finished, string status, int copied, int skipped, int warnings, int failed)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO runs(name, source_url, source_list, target_url, target_list, engine,
                started_utc, finished_utc, status, copied, skipped, warnings, failed)
            VALUES($n,$su,$sl,$tu,$tl,$e,$st,$fi,$s,$c,$k,$w,$x);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$su", sourceUrl);
        cmd.Parameters.AddWithValue("$sl", sourceList);
        cmd.Parameters.AddWithValue("$tu", targetUrl);
        cmd.Parameters.AddWithValue("$tl", targetList);
        cmd.Parameters.AddWithValue("$e", engine);
        cmd.Parameters.AddWithValue("$st", started.ToString("o"));
        cmd.Parameters.AddWithValue("$fi", (object?)finished?.ToString("o") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$s", status);
        cmd.Parameters.AddWithValue("$c", copied);
        cmd.Parameters.AddWithValue("$k", skipped);
        cmd.Parameters.AddWithValue("$w", warnings);
        cmd.Parameters.AddWithValue("$x", failed);
        return (long)cmd.ExecuteScalar()!;
    }

    private static void InsertItem(SqliteConnection db, SqliteTransaction tx, long runId, string itemType,
        string sourcePath, string targetPath, string status, string? message, DateTime ts)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO run_items(run_id, item_type, source_path, target_path, status, message, size_bytes, duration_ms, item_url, ts)
            VALUES($r,$t,$sp,$tp,$s,$m,$b,$d,$u,$ts)
            """;
        cmd.Parameters.AddWithValue("$r", runId);
        cmd.Parameters.AddWithValue("$t", itemType);
        cmd.Parameters.AddWithValue("$sp", sourcePath);
        cmd.Parameters.AddWithValue("$tp", targetPath);
        cmd.Parameters.AddWithValue("$s", status);
        cmd.Parameters.AddWithValue("$m", (object?)message ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$b", 0);
        cmd.Parameters.AddWithValue("$d", 0);
        cmd.Parameters.AddWithValue("$u", targetPath);
        cmd.Parameters.AddWithValue("$ts", ts.ToString("o"));
        cmd.ExecuteNonQuery();
    }
}

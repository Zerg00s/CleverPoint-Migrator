using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace CleverPoint.Migrator.Core.History;

/// <summary>A command-line job: one job file, one or more runs (one per list).</summary>
public class MigrationJob
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>The job file as it was when the job was started.</summary>
    public string SpecJson { get; set; } = "";
    /// <summary>Queued | Running | Completed | CompletedWithIssues | Failed | Cancelled | Interrupted</summary>
    public string Status { get; set; } = "Queued";
    public int? Pid { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public DateTime? HeartbeatUtc { get; set; }
    public bool CancelRequested { get; set; }
    public string? Error { get; set; }
    public string? LogPath { get; set; }

    public bool IsFinished => Status is not ("Queued" or "Running");
}

/// <summary>Live numbers for one run, counted from its item rows.</summary>
public sealed record RunProgress(int Planned, int Processed, int Copied, int Skipped, int Warnings, int Failed,
    string? LastItem, DateTime? LastItemUtc);

/// <summary>
/// What the history database needs once more than one process uses it: the desktop app, the command
/// line, and the detached process that executes a command-line job.
///
/// A run records the process that owns it and a heartbeat, so "Running" can be told apart from "the
/// process died". Cancelling is a flag the owner reads with its heartbeat, which works across
/// processes. Jobs group the runs started from one job file.
/// </summary>
public partial class HistoryStore
{
    /// <summary>A run or job whose owner has not reported in for this long is treated as dead.</summary>
    public static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(90);

    /// <summary>Silence after which a run is given up on even though a process with its owner's ID exists.</summary>
    private static readonly TimeSpan LongSilence = TimeSpan.FromMinutes(10);

    private void UpgradeForSharedUse()
    {
        foreach (var column in new[]
                 {
                     "planned INT DEFAULT 0", "job_id TEXT", "owner_pid INT", "heartbeat_utc TEXT", "cancel_requested INT DEFAULT 0",
                 })
        {
            try { Exec($"ALTER TABLE runs ADD COLUMN {column}"); }
            catch (SqliteException) { /* column already there */ }
        }
        Exec("""
            CREATE TABLE IF NOT EXISTS jobs(
                id TEXT PRIMARY KEY, name TEXT, spec_json TEXT, status TEXT, pid INT,
                created_utc TEXT, started_utc TEXT, finished_utc TEXT, heartbeat_utc TEXT,
                cancel_requested INT DEFAULT 0, error TEXT, log_path TEXT);
            CREATE INDEX IF NOT EXISTS ix_runs_job ON runs(job_id);
            """);
        UpgradeForReports();
    }

    // ---- runs: ownership, heartbeat, cancel ----------------------------------------------------

    /// <summary>
    /// Called by the owner every few seconds while the run is active. Records the planned total once it
    /// is known and returns true when someone asked for the run (or its job) to be cancelled.
    /// </summary>
    public bool Heartbeat(long runId, int planned)
    {
        using var cmd = Command("""
            UPDATE runs SET heartbeat_utc=$h, planned=MAX(COALESCE(planned,0), $p) WHERE id=$id;
            SELECT COALESCE(r.cancel_requested,0) + COALESCE((SELECT j.cancel_requested FROM jobs j WHERE j.id=r.job_id),0)
            FROM runs r WHERE r.id=$id;
            """);
        cmd.Parameters.AddWithValue("$h", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$p", planned);
        cmd.Parameters.AddWithValue("$id", runId);
        return cmd.ExecuteScalar() is long flags && flags > 0;
    }

    /// <summary>Asks the owner of a running run to stop. Returns false when the run is not running.</summary>
    public bool RequestCancelRun(long runId)
    {
        using var cmd = Command("UPDATE runs SET cancel_requested=1 WHERE id=$id AND status='Running'");
        cmd.Parameters.AddWithValue("$id", runId);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Marks runs and jobs as Interrupted when the process that owned them is gone: it stopped
    /// reporting in, or its process ID no longer exists. Runs of a live process are left alone, so the
    /// desktop app starting up does not mislabel a command-line job in progress, and the other way round.
    /// </summary>
    /// <param name="includeOwnProcess">
    /// True at application start, when this process cannot own any run yet, so a row carrying its
    /// process ID is a leftover from an earlier process that had the same ID.
    /// </param>
    public int MarkOrphanedAsInterrupted(bool includeOwnProcess)
    {
        var now = DateTime.UtcNow;
        var changed = 0;
        foreach (var table in new[]
                 {
                     ("runs", "id", "owner_pid", "status='Running'"),
                     ("jobs", "id", "pid", "status IN ('Queued','Running')"),
                     ("report_runs", "id", "pid", "status IN ('Queued','Running')"),
                 })
        {
            var (name, key, pidColumn, active) = table;
            var orphans = new List<object>();
            using (var select = Command($"SELECT {key}, {pidColumn}, heartbeat_utc FROM {name} WHERE {active}"))
            using (var r = select.ExecuteReader())
            {
                while (r.Read())
                {
                    int? pid = r.IsDBNull(1) ? null : r.GetInt32(1);
                    DateTime? beat = r.IsDBNull(2) ? null : DateTime.Parse(r.GetString(2)).ToUniversalTime();
                    // A live process that merely went quiet (the computer slept) keeps its run. Only a
                    // very long silence overrides a process ID that still exists, since IDs get reused.
                    // No owner yet is normal for a job that was queued a moment ago and whose process is
                    // still starting; it only counts as lost once that has gone on for a while.
                    var gone = pid is null
                        ? beat is null || now - beat.Value > HeartbeatTimeout
                        : !ProcessExists(pid.Value) || (includeOwnProcess && pid.Value == Environment.ProcessId);
                    var silent = beat is null || now - beat.Value > LongSilence;
                    if (gone || silent) orphans.Add(r.GetValue(0));
                }
            }
            foreach (var id in orphans)
            {
                using var update = Command($"UPDATE {name} SET status='Interrupted', finished_utc=COALESCE(finished_utc,$f) WHERE {key}=$id AND {active}");
                update.Parameters.AddWithValue("$f", now.ToString("o"));
                update.Parameters.AddWithValue("$id", id);
                changed += update.ExecuteNonQuery();
            }
        }
        return changed;
    }

    /// <summary>Kept for callers written before runs had owners: at start-up, everything not owned by a live process.</summary>
    public int MarkRunningAsInterrupted() => MarkOrphanedAsInterrupted(includeOwnProcess: true);

    private static bool ProcessExists(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; }   // exists, but belongs to another user
    }

    /// <summary>Counts from the item rows, so they are current while the run is still going.</summary>
    public RunProgress GetRunProgress(long runId)
    {
        int planned = 0, processed = 0, copied = 0, skipped = 0, warnings = 0, failed = 0;
        using (var cmd = Command("SELECT COALESCE(planned,0) FROM runs WHERE id=$id"))
        {
            cmd.Parameters.AddWithValue("$id", runId);
            if (cmd.ExecuteScalar() is long p) planned = (int)p;
        }
        using (var cmd = Command("""
            SELECT status, COUNT(*), SUM(CASE WHEN item_type IN ('Item','File','Folder','OneNote') THEN 1 ELSE 0 END)
            FROM run_items WHERE run_id=$id GROUP BY status
            """))
        {
            cmd.Parameters.AddWithValue("$id", runId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var count = r.GetInt32(1);
                processed += r.IsDBNull(2) ? 0 : r.GetInt32(2);
                switch (r.GetString(0))
                {
                    case "Copied": copied = count; break;
                    case "Skipped": skipped = count; break;
                    case "Warning": warnings = count; break;
                    case "Failed": failed = count; break;
                }
            }
        }
        string? last = null;
        DateTime? lastUtc = null;
        using (var cmd = Command("SELECT source_path, ts FROM run_items WHERE run_id=$id ORDER BY id DESC LIMIT 1"))
        {
            cmd.Parameters.AddWithValue("$id", runId);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                last = r.GetString(0);
                lastUtc = r.IsDBNull(1) ? null : DateTime.Parse(r.GetString(1)).ToUniversalTime();
            }
        }
        return new RunProgress(planned, processed, copied, skipped, warnings, failed, last, lastUtc);
    }

    /// <summary>One page of a run's item rows, newest last. Statuses: Copied, Skipped, Warning, Failed, Info.</summary>
    public List<(long Id, string ItemType, string SourcePath, string TargetPath, string Status, string? Message, DateTime? WhenUtc)>
        GetItemsPage(long runId, IReadOnlyCollection<string>? statuses, int limit, long afterId = 0)
    {
        var items = new List<(long, string, string, string, string, string?, DateTime?)>();
        var filter = "";
        using var cmd = Command("");
        if (statuses is { Count: > 0 })
        {
            var names = statuses.Select((_, i) => $"$s{i}").ToList();
            filter = $" AND status IN ({string.Join(",", names)})";
            var index = 0;
            foreach (var status in statuses) cmd.Parameters.AddWithValue($"$s{index++}", status);
        }
        cmd.CommandText = "SELECT id,item_type,source_path,target_path,status,message,ts FROM run_items " +
                          $"WHERE run_id=$r AND id>$after{filter} ORDER BY id LIMIT $l";
        cmd.Parameters.AddWithValue("$r", runId);
        cmd.Parameters.AddWithValue("$after", afterId);
        cmd.Parameters.AddWithValue("$l", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            items.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : DateTime.Parse(r.GetString(6)).ToUniversalTime()));
        return items;
    }

    public List<MigrationRun> GetRunsOfJob(string jobId)
    {
        var runs = new List<MigrationRun>();
        using var cmd = Command($"SELECT {RunColumns} FROM runs WHERE job_id=$j ORDER BY id");
        cmd.Parameters.AddWithValue("$j", jobId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) runs.Add(MapRun(r));
        return runs;
    }

    // ---- jobs --------------------------------------------------------------------------------

    private const string JobColumns = "id,name,spec_json,status,pid,created_utc,started_utc,finished_utc,heartbeat_utc,cancel_requested,error,log_path";

    public void CreateJob(MigrationJob job)
    {
        using var cmd = Command("""
            INSERT INTO jobs(id, name, spec_json, status, created_utc, heartbeat_utc, log_path)
            VALUES($id, $n, $spec, 'Queued', $now, $now, $log)
            """);
        cmd.Parameters.AddWithValue("$id", job.Id);
        cmd.Parameters.AddWithValue("$n", job.Name);
        cmd.Parameters.AddWithValue("$spec", job.SpecJson);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$log", (object?)job.LogPath ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public MigrationJob? GetJob(string id)
    {
        using var cmd = Command($"SELECT {JobColumns} FROM jobs WHERE id=$id");
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? MapJob(r) : null;
    }

    public List<MigrationJob> GetJobs(int limit = 50)
    {
        var jobs = new List<MigrationJob>();
        using var cmd = Command($"SELECT {JobColumns} FROM jobs ORDER BY created_utc DESC LIMIT $l");
        cmd.Parameters.AddWithValue("$l", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read()) jobs.Add(MapJob(r));
        return jobs;
    }

    /// <summary>The executing process claims the job. False when it was cancelled before it could start.</summary>
    public bool StartJob(string id)
    {
        using var cmd = Command("""
            UPDATE jobs SET status='Running', pid=$pid, started_utc=$now, heartbeat_utc=$now
            WHERE id=$id AND status='Queued' AND COALESCE(cancel_requested,0)=0
            """);
        cmd.Parameters.AddWithValue("$pid", Environment.ProcessId);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Called by the executing process between runs. Returns true when cancel was requested.</summary>
    public bool JobHeartbeat(string id)
    {
        using var cmd = Command("UPDATE jobs SET heartbeat_utc=$h WHERE id=$id; SELECT COALESCE(cancel_requested,0) FROM jobs WHERE id=$id;");
        cmd.Parameters.AddWithValue("$h", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() is long flag && flag > 0;
    }

    public void FinishJob(string id, string status, string? error = null)
    {
        using var cmd = Command("UPDATE jobs SET status=$s, finished_utc=$f, error=$e WHERE id=$id");
        cmd.Parameters.AddWithValue("$s", status);
        cmd.Parameters.AddWithValue("$f", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$e", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Asks a queued or running job to stop. A queued job is cancelled on the spot.</summary>
    public bool RequestCancelJob(string id)
    {
        using var cmd = Command("""
            UPDATE jobs SET cancel_requested=1,
                status=CASE WHEN status='Queued' THEN 'Cancelled' ELSE status END,
                finished_utc=CASE WHEN status='Queued' THEN $now ELSE finished_utc END
            WHERE id=$id AND status IN ('Queued','Running')
            """);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    private static MigrationJob MapJob(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        Name = r.IsDBNull(1) ? "" : r.GetString(1),
        SpecJson = r.IsDBNull(2) ? "" : r.GetString(2),
        Status = r.GetString(3),
        Pid = r.IsDBNull(4) ? null : r.GetInt32(4),
        CreatedUtc = DateTime.Parse(r.GetString(5)).ToUniversalTime(),
        StartedUtc = r.IsDBNull(6) ? null : DateTime.Parse(r.GetString(6)).ToUniversalTime(),
        FinishedUtc = r.IsDBNull(7) ? null : DateTime.Parse(r.GetString(7)).ToUniversalTime(),
        HeartbeatUtc = r.IsDBNull(8) ? null : DateTime.Parse(r.GetString(8)).ToUniversalTime(),
        CancelRequested = !r.IsDBNull(9) && r.GetInt32(9) != 0,
        Error = r.IsDBNull(10) ? null : r.GetString(10),
        LogPath = r.IsDBNull(11) ? null : r.GetString(11),
    };
}

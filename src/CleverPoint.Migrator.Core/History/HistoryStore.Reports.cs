using Microsoft.Data.Sqlite;

namespace CleverPoint.Migrator.Core.History;

/// <summary>
/// One run of a report. The rows it produced are files in <see cref="Folder"/>; this record is the
/// index that the desktop app and the command line both read to list runs and follow progress.
/// </summary>
public class ReportRun
{
    public string Id { get; set; } = "";
    /// <summary>The report's ID, for example "lists".</summary>
    public string Report { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>The report file as it was when the run was started.</summary>
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
    /// <summary>Where the rows and per-site records of this run are.</summary>
    public string Folder { get; set; } = "";
    public int SitesTotal { get; set; }
    public int SitesDone { get; set; }
    public int SitesFailed { get; set; }
    public long Rows { get; set; }
    /// <summary>What the run is doing right now, for anyone watching.</summary>
    public string? Current { get; set; }

    public bool IsFinished => Status is not ("Queued" or "Running");
    /// <summary>Stopped before every site was done, so "resume" has something to do.</summary>
    public bool CanResume => IsFinished && (Status != "Completed" || SitesFailed > 0);
}

public partial class HistoryStore
{
    private void UpgradeForReports()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS report_runs(
                id TEXT PRIMARY KEY, report TEXT, name TEXT, spec_json TEXT, status TEXT, pid INT,
                created_utc TEXT, started_utc TEXT, finished_utc TEXT, heartbeat_utc TEXT,
                cancel_requested INT DEFAULT 0, error TEXT, log_path TEXT, folder TEXT,
                sites_total INT DEFAULT 0, sites_done INT DEFAULT 0, sites_failed INT DEFAULT 0,
                row_count INT DEFAULT 0, current TEXT);
            """);
    }

    private const string ReportColumns =
        "id,report,name,spec_json,status,pid,created_utc,started_utc,finished_utc,heartbeat_utc,cancel_requested,error,log_path,folder," +
        "sites_total,sites_done,sites_failed,row_count,current";

    public void CreateReportRun(ReportRun run)
    {
        using var cmd = Command("""
            INSERT INTO report_runs(id, report, name, spec_json, status, created_utc, heartbeat_utc, log_path, folder)
            VALUES($id, $r, $n, $spec, 'Queued', $now, $now, $log, $folder)
            """);
        cmd.Parameters.AddWithValue("$id", run.Id);
        cmd.Parameters.AddWithValue("$r", run.Report);
        cmd.Parameters.AddWithValue("$n", run.Name);
        cmd.Parameters.AddWithValue("$spec", run.SpecJson);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$log", (object?)run.LogPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$folder", run.Folder);
        cmd.ExecuteNonQuery();
    }

    public ReportRun? GetReportRun(string id)
    {
        using var cmd = Command($"SELECT {ReportColumns} FROM report_runs WHERE id=$id");
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? MapReportRun(r) : null;
    }

    public List<ReportRun> GetReportRuns(int limit = 50)
    {
        var runs = new List<ReportRun>();
        using var cmd = Command($"SELECT {ReportColumns} FROM report_runs ORDER BY created_utc DESC LIMIT $l");
        cmd.Parameters.AddWithValue("$l", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read()) runs.Add(MapReportRun(r));
        return runs;
    }

    /// <summary>The executing process claims the run. False when it was cancelled before it could start, or is not waiting to start.</summary>
    public bool StartReportRun(string id)
    {
        using var cmd = Command("""
            UPDATE report_runs SET status='Running', pid=$pid, started_utc=COALESCE(started_utc,$now), finished_utc=NULL,
                heartbeat_utc=$now, error=NULL
            WHERE id=$id AND status='Queued' AND COALESCE(cancel_requested,0)=0
            """);
        cmd.Parameters.AddWithValue("$pid", Environment.ProcessId);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Puts a finished run back in the queue so it can be continued. False when it is still active.</summary>
    public bool RequeueReportRun(string id)
    {
        using var cmd = Command("""
            UPDATE report_runs SET status='Queued', pid=NULL, cancel_requested=0, heartbeat_utc=$now, current=NULL
            WHERE id=$id AND status NOT IN ('Queued','Running')
            """);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Called by the executing process every few seconds and after each site. Records where the run
    /// stands and returns true when someone asked for it to be cancelled.
    /// </summary>
    public bool ReportHeartbeat(string id, int sitesTotal, int sitesDone, int sitesFailed, long rows, string? current)
    {
        using var cmd = Command("""
            UPDATE report_runs SET heartbeat_utc=$h, sites_total=$t, sites_done=$d, sites_failed=$f, row_count=$rows, current=COALESCE($c, current) WHERE id=$id;
            SELECT COALESCE(cancel_requested,0) FROM report_runs WHERE id=$id;
            """);
        cmd.Parameters.AddWithValue("$h", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$t", sitesTotal);
        cmd.Parameters.AddWithValue("$d", sitesDone);
        cmd.Parameters.AddWithValue("$f", sitesFailed);
        cmd.Parameters.AddWithValue("$rows", rows);
        cmd.Parameters.AddWithValue("$c", (object?)current ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() is long flag && flag > 0;
    }

    public void FinishReportRun(string id, string status, string? error = null)
    {
        using var cmd = Command("UPDATE report_runs SET status=$s, finished_utc=$f, error=$e, current=NULL WHERE id=$id");
        cmd.Parameters.AddWithValue("$s", status);
        cmd.Parameters.AddWithValue("$f", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$e", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Asks a queued or running report to stop. A queued one is cancelled on the spot.</summary>
    public bool RequestCancelReportRun(string id)
    {
        using var cmd = Command("""
            UPDATE report_runs SET cancel_requested=1,
                status=CASE WHEN status='Queued' THEN 'Cancelled' ELSE status END,
                finished_utc=CASE WHEN status='Queued' THEN $now ELSE finished_utc END
            WHERE id=$id AND status IN ('Queued','Running')
            """);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public void RenameReportRun(string id, string name)
    {
        using var cmd = Command("UPDATE report_runs SET name=$n WHERE id=$id");
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public bool DeleteReportRun(string id)
    {
        using var cmd = Command("DELETE FROM report_runs WHERE id=$id AND status NOT IN ('Queued','Running')");
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    private static ReportRun MapReportRun(SqliteDataReader r)
    {
        static DateTime? When(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : DateTime.Parse(r.GetString(i)).ToUniversalTime();
        return new ReportRun
        {
            Id = r.GetString(0),
            Report = r.IsDBNull(1) ? "" : r.GetString(1),
            Name = r.IsDBNull(2) ? "" : r.GetString(2),
            SpecJson = r.IsDBNull(3) ? "" : r.GetString(3),
            Status = r.GetString(4),
            Pid = r.IsDBNull(5) ? null : r.GetInt32(5),
            CreatedUtc = When(r, 6) ?? DateTime.UtcNow,
            StartedUtc = When(r, 7),
            FinishedUtc = When(r, 8),
            HeartbeatUtc = When(r, 9),
            CancelRequested = !r.IsDBNull(10) && r.GetInt32(10) != 0,
            Error = r.IsDBNull(11) ? null : r.GetString(11),
            LogPath = r.IsDBNull(12) ? null : r.GetString(12),
            Folder = r.IsDBNull(13) ? "" : r.GetString(13),
            SitesTotal = r.IsDBNull(14) ? 0 : r.GetInt32(14),
            SitesDone = r.IsDBNull(15) ? 0 : r.GetInt32(15),
            SitesFailed = r.IsDBNull(16) ? 0 : r.GetInt32(16),
            Rows = r.IsDBNull(17) ? 0 : r.GetInt64(17),
            Current = r.IsDBNull(18) ? null : r.GetString(18),
        };
    }
}

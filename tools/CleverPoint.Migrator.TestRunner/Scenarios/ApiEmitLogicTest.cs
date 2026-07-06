using CleverPoint.Migrator.Core.MigrationApi;
using CleverPoint.Migrator.Core.Model;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Deterministic unit test (no tenant) for the Migration-API emit decision, the
/// fix for review finding C4. Before the fix, a file in a job that ended in a
/// fatal error / timeout was logged Copied even though nothing imported. The pure
/// DecideEmitStatus method now returns Failed for those items.
/// </summary>
public static class ApiEmitLogicTest
{
    public static Task RunAsync()
    {
        var okJob = Guid.NewGuid();
        var deadJob = Guid.NewGuid();   // ended in a fatal error / timeout
        var failedJobs = new HashSet<Guid> { deadJob };
        var failedRelPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Folder-A/dup.docx"] = "a file with that name already exists",
        };

        // 1. Healthy job, no per-file failure -> Copied.
        var s1 = MigrationApiEngine.DecideEmitStatus("File", "Folder-A/report.docx", okJob, failedJobs, failedRelPaths, out _);
        Program.Check("emit: healthy job file is Copied", s1 == ItemCopyStatus.Copied, s1.ToString());

        // 2. Healthy job, but this specific file failed -> Failed with the per-file reason.
        var s2 = MigrationApiEngine.DecideEmitStatus("File", "Folder-A/dup.docx", okJob, failedJobs, failedRelPaths, out var r2);
        Program.Check("emit: per-file failure is Failed", s2 == ItemCopyStatus.Failed && r2!.Contains("already exists"), $"{s2}: {r2}");

        // 3. THE C4 FIX: a file in a fatally-failed job must be Failed, not Copied.
        var s3 = MigrationApiEngine.DecideEmitStatus("File", "Folder-A/report.docx", deadJob, failedJobs, failedRelPaths, out var r3);
        Program.Check("emit: file in a FAILED job is Failed (C4 fix)", s3 == ItemCopyStatus.Failed, $"{s3}: {r3}");

        // 4. Folders in a failed job are also not confirmed -> Failed.
        var s4 = MigrationApiEngine.DecideEmitStatus("Folder", "Folder-A", deadJob, failedJobs, failedRelPaths, out _);
        Program.Check("emit: folder in a FAILED job is Failed (C4 fix)", s4 == ItemCopyStatus.Failed, s4.ToString());

        // 5. Folder in a healthy job -> Copied.
        var s5 = MigrationApiEngine.DecideEmitStatus("Folder", "Folder-A", okJob, failedJobs, failedRelPaths, out _);
        Program.Check("emit: folder in a healthy job is Copied", s5 == ItemCopyStatus.Copied, s5.ToString());

        // 6. THE H6 FIX: SPO names the failed file inconsistently. A leaf-name key and a
        //    web-relative-URL key must both still match the library-relative path.
        var leafKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["report.docx"] = "blocked file type" };
        var s6 = MigrationApiEngine.DecideEmitStatus("File", "Folder-A/report.docx", okJob, failedJobs, leafKey, out _);
        Program.Check("emit: leaf-name failure key still matches (H6)", s6 == ItemCopyStatus.Failed, s6.ToString());

        var urlKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Shared Documents/Folder-A/report.docx"] = "conflict" };
        var s7 = MigrationApiEngine.DecideEmitStatus("File", "Folder-A/report.docx", okJob, failedJobs, urlKey, out _);
        Program.Check("emit: web-relative-URL failure key still matches (H6)", s7 == ItemCopyStatus.Failed, s7.ToString());

        // 7. A DIFFERENT file must NOT match (no false Failed from the leaf heuristic).
        var s8 = MigrationApiEngine.DecideEmitStatus("File", "Folder-A/other.docx", okJob, failedJobs, leafKey, out _);
        Program.Check("emit: unrelated file stays Copied", s8 == ItemCopyStatus.Copied, s8.ToString());

        return Task.CompletedTask;
    }
}

using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace CleverPoint.Migrator.Core.Diagnostics;

/// <summary>
/// Trace logging with a [CPMigrator] prefix so DebugView/DbgView picks it up,
/// plus the "Start capturing / Stop capturing" issue recorder: while active,
/// everything traced is also written to a file, and Stop produces a zip
/// bundle (log + environment snapshot, secrets never included) that users
/// can attach to a GitHub issue.
/// </summary>
public static class TraceLog
{
    private static TextWriterTraceListener? _capture;
    private static string? _capturePath;
    private static readonly object Gate = new();

    public static void Write(string area, string message) =>
        Trace.WriteLine($"[CPMigrator] {DateTime.UtcNow:HH:mm:ss.fff} [{area}] {message}");

    public static bool IsCapturing => _capture != null;

    /// <summary>Starts recording all trace output to a file ("Start capturing").</summary>
    public static string StartCapture(string folder)
    {
        lock (Gate)
        {
            StopCaptureListener();
            Directory.CreateDirectory(folder);
            _capturePath = Path.Combine(folder, $"capture-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            _capture = new TextWriterTraceListener(_capturePath, "cpmigrator-capture");
            Trace.Listeners.Add(_capture);
            Trace.AutoFlush = true;
            Write("Diagnostics", "capture started");
            return _capturePath;
        }
    }

    /// <summary>Stops recording and bundles the capture into a shareable zip ("Stop capturing").</summary>
    public static string? StopCapture(string? extraInfo = null)
    {
        lock (Gate)
        {
            if (_capture == null || _capturePath == null) return null;
            Write("Diagnostics", "capture stopped");
            StopCaptureListener();

            var zipPath = Path.ChangeExtension(_capturePath, ".zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(_capturePath, "capture.log");
                var info = new StringBuilder()
                    .AppendLine($"CleverPoint Migrator diagnostic bundle")
                    .AppendLine($"Captured (UTC): {DateTime.UtcNow:o}")
                    .AppendLine($"OS: {Environment.OSVersion}")
                    .AppendLine($".NET: {Environment.Version}")
                    .AppendLine($"64-bit: {Environment.Is64BitProcess}")
                    .AppendLine(extraInfo ?? "");
                var entry = zip.CreateEntry("environment.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.Write(info.ToString());
            }
            File.Delete(_capturePath);
            _capturePath = null;
            return zipPath;
        }
    }

    private static void StopCaptureListener()
    {
        if (_capture == null) return;
        Trace.Listeners.Remove(_capture);
        _capture.Flush();
        _capture.Dispose();
        _capture = null;
    }
}

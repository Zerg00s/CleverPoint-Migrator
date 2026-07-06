using System.Runtime.InteropServices;

namespace CleverPoint.Migrator.Ux.Services;

/// <summary>
/// Ensures only one copy of the app runs per user session. A second instance
/// would otherwise fight the first over the WebView2 user-data folder (a native
/// 0xC0000005 crash during browser sign-in) and split the shared state: the
/// history database, settings.json and the cached sign-in sessions.
/// </summary>
public static class SingleInstance
{
    private const string MutexName = "CleverPoint.Migrator.Ux.SingleInstance";
    private const string WindowTitle = "CleverPoint Migrator";

    // Held for the whole process lifetime; a static field keeps it off the GC's
    // radar so the lock survives until the app exits (or the process dies).
    private static Mutex? _mutex;

    /// <summary>
    /// True when this is the only running instance. When false the caller must
    /// exit immediately; the already-running window is brought to the foreground.
    /// </summary>
    public static bool Acquire()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexNameForUser(), out var isNew);
            if (isNew) return true;
        }
        catch
        {
            // If the named mutex can't be created, don't block startup over it.
            return true;
        }

        if (OperatingSystem.IsWindows()) FocusExisting();
        return false;
    }

    // Per-USER, cross-session name. The WebView2 profile, settings and history are per-user,
    // so a second SESSION of the same user (console + RDP, two desktops) must not open a second
    // instance, which would crash browser sign-in on the shared WebView2 folder. Global\ spans
    // sessions; appending the SID keeps different users independent.
    private static string MutexNameForUser()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
                if (!string.IsNullOrEmpty(sid)) return $"Global\\{MutexName}.{sid}";
            }
            catch { /* fall back to the plain per-session name */ }
        }
        return MutexName;
    }

    private static void FocusExisting()
    {
        try
        {
            var hwnd = FindWindow(null, WindowTitle);
            if (hwnd != IntPtr.Zero)
            {
                ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
            }
        }
        catch { /* focusing the existing window is best-effort */ }
    }

    private const int SW_RESTORE = 9;
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
}

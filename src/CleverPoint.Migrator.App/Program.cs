using CleverPoint.Migrator.App.Screens;

namespace CleverPoint.Migrator.App;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        ApplicationConfiguration.Initialize();

        // Friendly last-resort error handling: never a raw crash dialog.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => CrashHandler.Handle(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashHandler.Handle(e.ExceptionObject as Exception);

        using var splash = new SplashForm();
        splash.Show();
        Application.DoEvents();

        var main = new MainForm();
        main.Load += (_, _) => splash.Close();
        Application.Run(main);
    }
}

/// <summary>Writes a diagnostic bundle and shows a gentle error dialog instead of crashing.</summary>
public static class CrashHandler
{
    public static void Handle(Exception? ex)
    {
        if (ex == null) return;
        System.Diagnostics.Trace.WriteLine($"[CPMigrator] UNHANDLED: {ex}");
        try
        {
            var path = Path.Combine(Services.AppSettings.Folder, "crash-reports");
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt"), ex.ToString());
        }
        catch { /* reporting must never crash the crash handler */ }

        MessageBox.Show(
            "Something unexpected happened, but your migrations and history are safe.\n\n" +
            $"Details were saved to the crash-reports folder so the issue can be fixed.\n\n{ex.Message}",
            "CleverPoint Migrator", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}

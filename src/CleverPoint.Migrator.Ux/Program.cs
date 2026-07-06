using CleverPoint.Migrator.Ux.Components;
using CleverPoint.Migrator.Ux.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Photino.Blazor;
using Photino.NET;

namespace CleverPoint.Migrator.Ux;

internal class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Only one instance per user session. A second one collides with the first
        // over the WebView2 user-data folder (browser sign-in then crashes natively)
        // and over the shared history db / settings / sign-in cache.
        if (!SingleInstance.Acquire())
            return; // already running; its window has been brought to the foreground

        var builder = PhotinoBlazorAppBuilder.CreateDefault(args);

        builder.Services.AddLogging();
        builder.Services.AddFluentUIComponents();

        // Real app state + the migration engine's history store (from Core).
        builder.Services.AddSingleton<UxSettings>();
        builder.Services.AddSingleton<HistoryService>();
        builder.Services.AddSingleton<AppStatusService>();
        builder.Services.AddSingleton<ActivityService>();
        builder.Services.AddSingleton<SiteBrowser>();
        builder.Services.AddSingleton<BrowserSignIn>();
        builder.Services.AddSingleton<UxMigrationService>();
        builder.Services.AddSingleton<PendingMigration>();
        builder.Services.AddSingleton<DragState>();
        builder.Services.AddSingleton<ExplorerState>();
        builder.Services.AddSingleton<MigrationRunner>();
        builder.Services.AddSingleton<UxMappingStore>();
        builder.Services.AddSingleton<UpdateService>();

        builder.RootComponents.Add<App>("#app");

        var app = builder.Build();

        // A run still "Running" in history can only be a leftover from a session that
        // closed mid-copy, so reconcile it to Interrupted on startup.
        (app.Services.GetService(typeof(HistoryService)) as HistoryService)?.ReconcileOrphanedRuns();

        app.MainWindow
            .SetTitle("CleverPoint Migrator")
            .SetUseOsDefaultSize(false)
            .SetSize(1320, 880)
            .SetMinSize(600, 400)
            .SetResizable(true)
            .SetDevToolsEnabled(true)
            .Center();

        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "favicon.ico");
            if (File.Exists(iconPath))
                app.MainWindow.SetIconFile(iconPath);
        }
        catch { /* non-fatal */ }

        AppDomain.CurrentDomain.UnhandledException += (_, error) =>
        {
            AppLog.Error("UnhandledException", error.ExceptionObject);
            app.MainWindow.ShowMessage("Unexpected error", error.ExceptionObject?.ToString() ?? "Unknown error");
        };

        // Faulted background tasks (migration workers, etc.) surface here; record
        // them to the log and mark observed so they don't tear down the process.
        TaskScheduler.UnobservedTaskException += (_, error) =>
        {
            AppLog.Error("UnobservedTaskException", error.Exception);
            error.SetObserved();
        };

        // Closing while any migration is queued or running warns first; the run
        // keeps going unless the user confirms. Returning true cancels the close.
        // We ask the MigrationRunner (the real live state) rather than
        // ActivityService, whose IsBusy flag is never set anywhere.
        var runner = app.Services.GetService(typeof(MigrationRunner)) as MigrationRunner;
        app.MainWindow.WindowClosing += (_, _) =>
        {
            var active = runner?.ActiveCount ?? 0;
            if (active == 0) return false;
            var result = app.MainWindow.ShowMessage(
                "Migration in progress",
                active == 1
                    ? "A migration is still running. Close the app and stop it? Everything copied so far stays in place."
                    : $"{active} migrations are still running or queued. Close the app and stop them? Everything copied so far stays in place.",
                PhotinoDialogButtons.YesNo, PhotinoDialogIcon.Warning);
            if (result != PhotinoDialogResult.Yes) return true;   // No: keep the app open
            runner?.CancelAll();   // Yes: signal every worker to stop before the process exits
            return false;          // allow the close
        };

        app.Run();
    }
}

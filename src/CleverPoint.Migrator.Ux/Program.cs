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
        builder.Services.AddSingleton<MigrationRunner>();

        builder.RootComponents.Add<App>("#app");

        var app = builder.Build();

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
            app.MainWindow.ShowMessage("Unexpected error", error.ExceptionObject?.ToString() ?? "Unknown error");

        // Closing during a migration warns first; the run keeps going unless the
        // user confirms. Returning true cancels the close.
        var activity = app.Services.GetService(typeof(ActivityService)) as ActivityService;
        app.MainWindow.WindowClosing += (_, _) =>
        {
            if (activity is null || !activity.IsBusy) return false;
            var result = app.MainWindow.ShowMessage(
                "Migration in progress",
                "A migration is still running. Close the app and stop it? Everything copied so far stays in place.",
                PhotinoDialogButtons.YesNo, PhotinoDialogIcon.Warning);
            return result != PhotinoDialogResult.Yes; // cancel close unless Yes
        };

        app.Run();
    }
}

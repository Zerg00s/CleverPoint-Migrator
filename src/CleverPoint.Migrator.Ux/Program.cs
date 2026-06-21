using CleverPoint.Migrator.Ux.Components;
using CleverPoint.Migrator.Ux.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Photino.Blazor;

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
        builder.Services.AddSingleton<SiteBrowser>();
        builder.Services.AddSingleton<UxMigrationService>();

        builder.RootComponents.Add<App>("#app");

        var app = builder.Build();

        app.MainWindow
            .SetTitle("CleverPoint Migrator")
            .SetUseOsDefaultSize(false)
            .SetSize(1320, 880)
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

        app.Run();
    }
}

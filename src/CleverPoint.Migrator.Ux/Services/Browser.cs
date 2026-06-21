using System.Diagnostics;

namespace CleverPoint.Migrator.Ux.Services;

/// <summary>
/// Opens a URL in the operating system's DEFAULT browser rather than inside the
/// Photino WebView (a target="_blank" link would otherwise load in-app or pop a
/// bare WebView dialog). UseShellExecute routes through the OS handler:
/// the default browser on Windows, xdg-open on Linux.
/// </summary>
public static class Browser
{
    public static void Open(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Last-resort fallbacks for environments where UseShellExecute can't
            // resolve a handler directly.
            try
            {
                if (OperatingSystem.IsWindows())
                    Process.Start(new ProcessStartInfo("cmd", $"/c start \"\" \"{url}\"") { CreateNoWindow = true });
                else if (OperatingSystem.IsLinux())
                    Process.Start("xdg-open", url);
            }
            catch { /* nothing else we can do */ }
        }
    }
}

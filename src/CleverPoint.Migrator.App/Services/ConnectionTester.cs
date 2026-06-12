using System.Security.Cryptography.X509Certificates;

namespace CleverPoint.Migrator.App.Services;

/// <summary>
/// Connection health: probes a saved connection by reading the site title,
/// records the verdict + timestamp on the connection, and captures the
/// certificate expiry for app+certificate mode. Browser connections only
/// test interactively (a silent launch check must never pop sign-in windows).
/// </summary>
public static class ConnectionTester
{
    public static async Task<(bool Ok, string Message)> TestAsync(
        IWin32Window? owner, AppSettings settings, SavedConnection connection, bool allowInteractive)
    {
        try
        {
            if (connection.AuthMode == "AppCertificate")
                CaptureCertExpiry(connection);

            if (connection.AuthMode == "Browser" && !allowInteractive)
            {
                connection.LastStatus = "Signs in on use";
                return (true, "Browser connections sign in when used.");
            }

            var conn = ConnectionResolver.Resolve(owner ?? new WindowWrapper(), settings, connection.SiteUrl);
            using var doc = await conn.Rest.GetJsonAsync($"{conn.SiteUrl}/_api/web?$select=Title");
            var title = doc.RootElement.GetProperty("Title").GetString();
            connection.LastStatus = $"Connected ({title})";
            connection.LastVerifiedUtc = DateTime.UtcNow;
            return (true, $"Connected to '{title}'.");
        }
        catch (Exception ex) when (connection.AuthMode == "Browser"
            && (ex.Message.Contains("401") || ex.Message.Contains("403") || ex.Message.Contains("Unauthorized")))
        {
            // An expired sign-in session is normal for browser mode, not an
            // error state. The next use (or Reconnect) pops the sign-in again.
            ConnectionResolver.InvalidateBrowserSession(connection.SiteUrl);
            connection.LastStatus = "Sign-in expired - you'll be asked to sign in on next use";
            connection.LastVerifiedUtc = DateTime.UtcNow;
            return (true, "The sign-in session expired. Use Reconnect, or just use the connection and sign in when prompted.");
        }
        catch (Exception ex)
        {
            var brief = ex.Message.Length > 160 ? ex.Message[..160] : ex.Message;
            connection.LastStatus = $"Failed: {brief}";
            connection.LastVerifiedUtc = DateTime.UtcNow;
            return (false, brief);
        }
    }

    /// <summary>Force a fresh sign-in / token (Reconnect button).</summary>
    public static Task<(bool Ok, string Message)> ReconnectAsync(IWin32Window owner, AppSettings settings, SavedConnection connection)
    {
        ConnectionResolver.InvalidateBrowserSession(connection.SiteUrl);
        return TestAsync(owner, settings, connection, allowInteractive: true);
    }

    /// <summary>Launch-time sweep: silently verifies app+certificate connections.</summary>
    public static async Task<int> VerifyAllAtLaunchAsync(AppSettings settings)
    {
        var failures = 0;
        foreach (var connection in settings.Connections)
        {
            var (ok, _) = await TestAsync(null, settings, connection, allowInteractive: false);
            if (!ok) failures++;
        }
        settings.Save();
        return failures;
    }

    private static void CaptureCertExpiry(SavedConnection connection)
    {
        try
        {
            if (!File.Exists(connection.CertPfxPath)) return;
            var password = string.IsNullOrEmpty(connection.CertPasswordProtected) ? "" : AppSettings.Unprotect(connection.CertPasswordProtected);
            using var cert = new X509Certificate2(connection.CertPfxPath, password, X509KeyStorageFlags.EphemeralKeySet);
            connection.CertExpiresUtc = cert.NotAfter.ToUniversalTime();
        }
        catch { /* expiry display is best-effort */ }
    }

    public static string ExpiryText(SavedConnection connection) => connection.AuthMode == "Browser"
        ? "Session-based"
        : connection.CertExpiresUtc is { } e
            ? e < DateTime.UtcNow ? $"EXPIRED {e:yyyy-MM-dd}" : $"Cert until {e:yyyy-MM-dd}"
            : "";

    private sealed class WindowWrapper : IWin32Window
    {
        public IntPtr Handle => IntPtr.Zero;
    }
}

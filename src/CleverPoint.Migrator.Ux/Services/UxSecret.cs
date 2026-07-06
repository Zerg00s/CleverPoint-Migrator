using System.Security.Cryptography;
using System.Text;

namespace CleverPoint.Migrator.Ux.Services;

/// <summary>
/// Protects small secrets (currently the certificate password) at rest in
/// settings.json using Windows DPAPI (CurrentUser). The format matches the
/// WinForms app (base64 of a DPAPI blob) so the shared settings file stays
/// compatible. Unprotect tolerates legacy PLAINTEXT values written by earlier
/// builds and returns them unchanged, so existing connections keep working.
/// On non-Windows DPAPI is unavailable, so values pass through unprotected
/// (certificate auth on Linux/WSL is a developer scenario).
/// </summary>
public static class UxSecret
{
    public static string Protect(string secret)
    {
        if (string.IsNullOrEmpty(secret) || !OperatingSystem.IsWindows()) return secret ?? "";
        try
        {
            return Convert.ToBase64String(
                ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser));
        }
        catch
        {
            // DPAPI unavailable for some reason: better to keep the value usable than to
            // lose it. This is the same trade-off the sign-in session cache makes.
            return secret;
        }
    }

    public static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored) || !OperatingSystem.IsWindows()) return stored ?? "";
        try
        {
            return Encoding.UTF8.GetString(
                ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser));
        }
        catch
        {
            // Not a DPAPI blob (legacy plaintext) or protected by another user: treat it as
            // the literal value so pre-upgrade connections still authenticate.
            return stored;
        }
    }

    /// <summary>True when the stored value is a DPAPI blob this user can decrypt (i.e. already protected).</summary>
    public static bool IsProtected(string stored)
    {
        if (string.IsNullOrEmpty(stored) || !OperatingSystem.IsWindows()) return false;
        try
        {
            ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser);
            return true;
        }
        catch { return false; }
    }
}

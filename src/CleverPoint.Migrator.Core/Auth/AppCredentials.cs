namespace CleverPoint.Migrator.Core.Auth;

/// <summary>
/// App-only credentials for one tenant. Loadable from the plain-text secrets
/// files used across this project ("Tenant ID: ...", "App ID: ...",
/// "Cert Path: ...", "Cert Password: ..." lines).
/// </summary>
public class AppCredentials
{
    public string TenantId { get; set; } = "";
    public string AppId { get; set; } = "";
    public string AppSecret { get; set; } = "";
    public string CertPfxPath { get; set; } = "";
    public string CertPassword { get; set; } = "";

    public static AppCredentials LoadFromFile(string path)
    {
        var creds = new AppCredentials();
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;

        foreach (var line in File.ReadAllLines(path))
        {
            var sep = line.IndexOf(':');
            if (sep < 0) continue;
            var key = line[..sep].Trim().ToLowerInvariant();
            var value = line[(sep + 1)..].Trim();

            switch (key)
            {
                case "tenant id": creds.TenantId = value; break;
                case "app id": creds.AppId = value; break;
                case "app secret": creds.AppSecret = value; break;
                case "cert path": creds.CertPfxPath = Path.GetFullPath(Path.Combine(dir, value)); break;
                case "cert password": creds.CertPassword = value; break;
            }
        }

        if (string.IsNullOrEmpty(creds.TenantId) || string.IsNullOrEmpty(creds.AppId))
            throw new InvalidOperationException($"Could not parse Tenant ID / App ID from '{path}'.");
        return creds;
    }
}

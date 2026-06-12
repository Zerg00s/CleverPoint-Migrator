using CleverPoint.Migrator.App.Services;
using CleverPoint.Migrator.App.Theme;

namespace CleverPoint.Migrator.App.Screens;

/// <summary>
/// Guided Azure app provisioning: sign in as Global Admin, name the app, and
/// the wizard does the rest (permissions, consent, secret, certificate) and
/// saves the result straight into the app's remembered connections.
/// </summary>
public class AppRegistrationWizard : Form
{
    private readonly AppSettings _settings;
    private readonly AppRegistrationService _service = new();
    private readonly TextBox _appName = new() { Text = "CleverPoint Migrator", Width = 320 };
    private readonly Button _go = new() { Text = "Sign in and set everything up", AutoSize = true, Padding = new Padding(18, 10, 18, 10), BackColor = Brand.Accent, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Bottom, Height = 220, BorderStyle = BorderStyle.None, BackColor = Brand.SurfaceAlt };

    public AppRegistrationWizard(AppSettings settings)
    {
        _settings = settings;
        Text = "Set up an Azure app - CleverPoint Migrator";
        Size = new Size(640, 520);
        MinimumSize = new Size(560, 460);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Brand.Surface;
        Font = Brand.Body;

        Controls.Add(new Label
        {
            Text = "This wizard signs you in as a Global Admin (browser window), creates or\n" +
                   "reuses an app registration, grants SharePoint application permissions\n" +
                   "with admin consent, and generates a certificate. The result is saved as\n" +
                   "a ready-to-use connection. Safe to run again any time.",
            AutoSize = true, ForeColor = Brand.TextSecondary, Location = new Point(20, 16),
        });
        Controls.Add(new Label { Text = "App registration name", AutoSize = true, Location = new Point(20, 96) });
        _appName.Location = new Point(180, 92);
        Controls.Add(_appName);
        _go.Location = new Point(20, 132);
        _go.FlatAppearance.BorderSize = 0;
        Controls.Add(_go);
        Controls.Add(_log);

        _service.OnProgress += msg => BeginInvoke(() => _log.AppendText(msg + Environment.NewLine));
        _go.Click += async (_, _) => await RunAsync();
    }

    private async Task RunAsync()
    {
        _go.Enabled = false;
        try
        {
            _log.AppendText("Waiting for the browser sign-in..." + Environment.NewLine);
            var admin = await _service.SignInAsync();
            _log.AppendText($"Signed in as {admin}{Environment.NewLine}");

            var result = await _service.ProvisionAsync(_appName.Text.Trim(),
                Path.Combine(AppSettings.Folder, "certificates"));

            _settings.Connections.RemoveAll(c => c.AppId == result.AppId);
            _settings.Connections.Add(new SavedConnection
            {
                Name = $"{result.TenantName} ({_appName.Text.Trim()})",
                SiteUrl = result.SpoUrl,
                AuthMode = "AppCertificate",
                TenantId = result.TenantId,
                AppId = result.AppId,
                CertPfxPath = result.PfxPath,
                CertPasswordProtected = AppSettings.Protect(result.PfxPassword),
            });
            _settings.Save();

            _log.AppendText(Environment.NewLine +
                $"All done. Connection saved for {result.SpoUrl}{Environment.NewLine}" +
                $"App ID: {result.AppId}{Environment.NewLine}" +
                $"Certificate: {result.CertThumbprint} (PFX stored locally, password protected){Environment.NewLine}" +
                $"Client secret (for scripts; store it somewhere safe): {result.Secret}{Environment.NewLine}" +
                $"Secret expires: {result.SecretExpires}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            _log.AppendText($"{Environment.NewLine}Something went wrong: {ex.Message}{Environment.NewLine}");
        }
        finally
        {
            _go.Enabled = true;
        }
    }
}

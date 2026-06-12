using CleverPoint.Migrator.App.Services;
using CleverPoint.Migrator.App.Theme;
using CleverPoint.Migrator.Core.History;
using CleverPoint.Migrator.Core.Model;

namespace CleverPoint.Migrator.App.Screens;

/// <summary>
/// Settings, split into friendly sub-tabs. Common things up front; advanced
/// knobs tucked into their own tab; maintenance (clear history / cache) at
/// the end. Presets save/load lives here as two subtle buttons.
/// </summary>
public class SettingsScreen : UserControl
{
    private readonly AppSettings _settings;

    public SettingsScreen(AppSettings settings)
    {
        _settings = settings;
        BackColor = Brand.Surface;
        Padding = new Padding(16);

        var tabs = new TabControl { Dock = DockStyle.Fill, Font = Brand.Body };
        tabs.TabPages.Add(BuildGeneralTab());
        tabs.TabPages.Add(BuildConnectionsTab());
        tabs.TabPages.Add(BuildPerformanceTab());
        tabs.TabPages.Add(BuildAdvancedTab());
        tabs.TabPages.Add(BuildMaintenanceTab());
        tabs.TabPages.Add(BuildAboutTab());
        Controls.Add(tabs);
    }

    private TabPage BuildAboutTab()
    {
        var page = NewPage("About");
        page.Controls.Add(new Label
        {
            Text = $"CleverPoint Migrator  v{Application.ProductVersion.Split('+')[0]}\n" +
                   "Created by Denis Molodtsov, CleverPoint Solutions Inc.\n\n" +
                   "SharePoint Online migrations with REST, CSOM and the\nSPO Migration API. No Graph, no nonsense.",
            AutoSize = true, Location = new Point(20, 20), Font = Brand.Body,
        });
        var updateStatus = new Label { Text = "Checking for updates...", AutoSize = true, ForeColor = Brand.TextSecondary, Location = new Point(20, 124) };
        page.Controls.Add(updateStatus);
        _ = CheckForUpdatesAsync(updateStatus);

        var help = LinkButton("Help and documentation (GitHub)", 156);
        help.Click += (_, _) => OpenUrl("https://github.com/Zerg00s/CleverPoint-Migrator");
        var report = LinkButton("Report a problem (GitHub Issues)", 188);
        report.Click += (_, _) => OpenUrl("https://github.com/Zerg00s/CleverPoint-Migrator/issues");
        page.Controls.Add(help);
        page.Controls.Add(report);
        return page;
    }

    /// <summary>Non-blocking version check against GitHub releases.</summary>
    private static async Task CheckForUpdatesAsync(Label status)
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("CleverPointMigrator");
            var json = await http.GetStringAsync("https://api.github.com/repos/Zerg00s/CleverPoint-Migrator/releases/latest");
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var latest = doc.RootElement.GetProperty("tag_name").GetString()?.TrimStart('v') ?? "";
            var current = Application.ProductVersion.Split('+')[0];
            if (Version.TryParse(latest, out var l) && Version.TryParse(current, out var c) && l > c)
            {
                status.Text = $"Version {latest} is available - click to download";
                status.ForeColor = Brand.Accent;
                status.Cursor = Cursors.Hand;
                status.Click += (_, _) => OpenUrl("https://github.com/Zerg00s/CleverPoint-Migrator/releases/latest");
            }
            else
            {
                status.Text = "Up to date";
                status.ForeColor = Brand.Ok;
            }
        }
        catch
        {
            status.Text = "";   // offline or no releases yet; stay quiet
        }
    }

    private static void OpenUrl(string url) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });

    private TabPage BuildGeneralTab()
    {
        var page = NewPage("General");
        var toasts = AddCheck(page, "Show a notification when a migration finishes", _settings.ShowCompletionToasts, 20);
        var tray = AddCheck(page, "Minimize to the system tray", _settings.MinimizeToTray, 50);
        var startup = AddCheck(page, "Start with Windows", _settings.StartWithWindows, 80);

        AddSave(page, () =>
        {
            _settings.ShowCompletionToasts = toasts.Checked;
            _settings.MinimizeToTray = tray.Checked;
            _settings.StartWithWindows = startup.Checked;
            StartupRegistration.Apply(startup.Checked);
        });

        // Subtle presets affordance.
        var savePreset = LinkButton("Save settings as template...", 130);
        savePreset.Click += (_, _) =>
        {
            using var dialog = new SaveFileDialog { Filter = "Migration template|*.json", FileName = "migration-template.json" };
            if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
                SettingsPresets.Export(new CopyOptions(), Path.GetFileNameWithoutExtension(dialog.FileName), dialog.FileName);
        };
        var loadPreset = LinkButton("Load template...", 160);
        loadPreset.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = "Migration template|*.json" };
            if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
            {
                var (name, _) = SettingsPresets.Import(dialog.FileName);
                MessageBox.Show(FindForm(), $"Template '{name}' loaded. It will be used for the next migration.",
                    "Template loaded", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        };
        page.Controls.Add(savePreset);
        page.Controls.Add(loadPreset);
        return page;
    }

    private TabPage BuildConnectionsTab()
    {
        var page = NewPage("Connections");
        var list = new ListBox { Location = new Point(20, 20), Size = new Size(420, 220) };
        foreach (var c in _settings.Connections)
            list.Items.Add($"{c.Name}  ({c.AuthMode})  {c.SiteUrl}");
        page.Controls.Add(list);
        page.Controls.Add(new Label
        {
            Text = "Add an app+certificate connection for unattended copies, or sign in\nwith your browser when starting a migration.",
            AutoSize = true, ForeColor = Brand.TextSecondary, Location = new Point(20, 252),
        });
        var provision = LinkButton("Set up a NEW Azure app for me (Global Admin wizard)...", 296);
        provision.Click += (_, _) =>
        {
            using var wizard = new AppRegistrationWizard(_settings);
            wizard.ShowDialog(FindForm());
        };
        page.Controls.Add(provision);

        var add = LinkButton("Add an existing app + certificate connection...", 328);
        add.Click += (_, _) =>
        {
            using var editor = new ConnectionEditor(_settings);
            if (editor.ShowDialog(FindForm()) == DialogResult.OK)
            {
                list.Items.Clear();
                foreach (var c in _settings.Connections)
                    list.Items.Add($"{c.Name}  ({c.AuthMode})  {c.SiteUrl}");
            }
        };
        page.Controls.Add(add);
        return page;
    }

    private TabPage BuildPerformanceTab()
    {
        var page = NewPage("Performance");
        page.Controls.Add(new Label { Text = "Parallel migrations (1-3)", AutoSize = true, Location = new Point(20, 24) });
        var parallel = new NumericUpDown { Minimum = 1, Maximum = 3, Value = Math.Clamp(_settings.MaxParallelMigrations, 1, 3), Location = new Point(220, 20), Width = 70 };
        page.Controls.Add(parallel);
        page.Controls.Add(new Label { Text = "Request rate (how hard to push SharePoint)", AutoSize = true, Location = new Point(20, 60) });
        var tiers = new (string Label, double Rps)[]
        {
            ("Automatic - adapt to throttling (recommended)", 0),
            ("Low - a few requests (4/s)", 4),
            ("Medium - many requests (8/s)", 8),
            ("High - a lot of requests (16/s)", 16),
            ("Very high - maximum push (32/s)", 32),
        };
        var radios = new List<RadioButton>();
        for (var i = 0; i < tiers.Length; i++)
        {
            var radio = new RadioButton
            {
                Text = tiers[i].Label, AutoSize = true,
                Location = new Point(36, 88 + i * 28),
                Checked = Math.Abs(_settings.MaxRequestsPerSecond - tiers[i].Rps) < 0.1,
            };
            radios.Add(radio);
            page.Controls.Add(radio);
        }
        if (!radios.Any(r => r.Checked)) radios[0].Checked = true;
        page.Controls.Add(new Label
        {
            Text = "Extra migrations beyond the parallel limit queue up and wait.\nParallel runs share one request budget per tenant, so adding runs\nnever multiplies the load. Throttling pauses are handled for you.",
            AutoSize = true, ForeColor = Brand.TextSecondary, Location = new Point(20, 88 + tiers.Length * 28 + 8),
        });
        AddSave(page, () =>
        {
            _settings.MaxParallelMigrations = (int)parallel.Value;
            _settings.MaxRequestsPerSecond = tiers[radios.FindIndex(r => r.Checked)].Rps;
        });
        return page;
    }

    private TabPage BuildAdvancedTab()
    {
        var page = NewPage("Advanced");
        page.Controls.Add(new Label
        {
            Text = "Defaults work for almost everyone. These knobs exist for big or\nunusual migrations:\n\n" +
                   "- Items per Migration API package (default 200)\n" +
                   "- Large file threshold for streaming uploads (default 100 MB)\n" +
                   "- Versions to migrate per document (default: latest)\n" +
                   "- Self-healing: auto re-run incrementals, re-copy corrupt files\n\n" +
                   "Set these per migration in the New Migration screen under\n'Advanced options', or bake them into a saved template.",
            AutoSize = true, ForeColor = Brand.TextSecondary, Location = new Point(20, 20),
        });
        return page;
    }

    private TabPage BuildMaintenanceTab()
    {
        var page = NewPage("Maintenance");
        var clearHistory = LinkButton("Clear migration history...", 24);
        clearHistory.Click += (_, _) =>
        {
            var choice = MessageBox.Show(FindForm(),
                "Clear all migration history?\n\nDelta re-runs keep working (item maps are kept).",
                "Clear history", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (choice != DialogResult.Yes) return;
            using var store = new HistoryStore(AppSettings.HistoryDbPath);
            store.ClearHistory(wipeItemMaps: false);
        };
        var clearCache = LinkButton("Clear cache (site and list structure)", 60);
        clearCache.Click += (_, _) => AppSettings.ClearCache();
        var capture = LinkButton("Start capturing diagnostics for an issue report", 96);
        capture.Click += (_, _) =>
        {
            if (!Core.Diagnostics.TraceLog.IsCapturing)
            {
                var path = Core.Diagnostics.TraceLog.StartCapture(Path.Combine(AppSettings.Folder, "diagnostics"));
                capture.Text = "Stop capturing and create the report bundle";
                MessageBox.Show(FindForm(),
                    "Recording started. Reproduce the problem now, then come back here\nand click the button again to create the report bundle.\n\n" +
                    $"Recording to: {path}",
                    "Capturing diagnostics", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                var zip = Core.Diagnostics.TraceLog.StopCapture($"App version: {Application.ProductVersion}");
                capture.Text = "Start capturing diagnostics for an issue report";
                if (zip != null)
                {
                    MessageBox.Show(FindForm(),
                        $"Diagnostic bundle ready:\n{zip}\n\nAttach it to a GitHub issue at\ngithub.com/Zerg00s/CleverPoint-Migrator/issues (no secrets are included).",
                        "Bundle created", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{zip}\"") { UseShellExecute = true });
                }
            }
        };
        page.Controls.Add(clearHistory);
        page.Controls.Add(clearCache);
        page.Controls.Add(capture);
        return page;
    }

    private static TabPage NewPage(string title) => new(title) { BackColor = Brand.SurfaceAlt, Padding = new Padding(12) };

    private static CheckBox AddCheck(TabPage page, string text, bool value, int y)
    {
        var check = new CheckBox { Text = text, Checked = value, AutoSize = true, Location = new Point(20, y) };
        page.Controls.Add(check);
        return check;
    }

    private static Button LinkButton(string text, int y) => new()
    {
        Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat,
        ForeColor = Brand.Primary, BackColor = Color.Transparent,
        Location = new Point(20, y), Cursor = Cursors.Hand,
    };

    private void AddSave(TabPage page, Action apply)
    {
        var save = new Button
        {
            Text = "Save", Width = 90, Height = 34, FlatStyle = FlatStyle.Flat,
            BackColor = Brand.Accent, ForeColor = Color.White,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
        };
        save.FlatAppearance.BorderSize = 0;
        save.Location = new Point(page.Width - 110, page.Height - 54);
        save.Click += (_, _) => { apply(); _settings.Save(); };
        page.Controls.Add(save);
    }
}

/// <summary>HKCU Run key registration for "Start with Windows".</summary>
public static class StartupRegistration
{
    public static void Apply(bool enable)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (enable) key?.SetValue("CleverPointMigrator", Application.ExecutablePath);
            else key?.DeleteValue("CleverPointMigrator", throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[CPMigrator] startup registration: {ex.Message}");
        }
    }
}

/// <summary>Minimal app+certificate connection editor.</summary>
public class ConnectionEditor : Form
{
    public ConnectionEditor(AppSettings settings)
    {
        Text = "Add connection";
        Size = new Size(520, 320);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false;
        BackColor = Brand.Surface;
        Font = Brand.Body;

        var fields = new (string Label, TextBox Box)[]
        {
            ("Name", new TextBox()),
            ("Site URL", new TextBox()),
            ("Tenant ID", new TextBox()),
            ("App ID", new TextBox()),
            ("PFX path", new TextBox()),
            ("PFX password", new TextBox { UseSystemPasswordChar = true }),
        };
        for (var i = 0; i < fields.Length; i++)
        {
            Controls.Add(new Label { Text = fields[i].Label, AutoSize = true, Location = new Point(16, 20 + i * 36) });
            fields[i].Box.SetBounds(130, 16 + i * 36, i == 4 ? 300 : 350, 26);
            Controls.Add(fields[i].Box);
        }

        // PFX path gets a real file browser.
        var browse = new Button { Text = "Browse...", AutoSize = true, FlatStyle = FlatStyle.Flat, Location = new Point(436, 14 + 4 * 36) };
        browse.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = "Certificate files (*.pfx)|*.pfx|All files|*.*" };
            if (dialog.ShowDialog(this) == DialogResult.OK)
                fields[4].Box.Text = dialog.FileName;
        };
        Controls.Add(browse);

        var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, AutoSize = true, Padding = new Padding(14, 4, 14, 4), FlatStyle = FlatStyle.Flat, BackColor = Brand.Accent, ForeColor = Color.White };
        ok.FlatAppearance.BorderSize = 0;
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Padding = new Padding(14, 4, 14, 4), FlatStyle = FlatStyle.Flat };
        ok.Location = new Point(300, 240);
        cancel.Location = new Point(396, 240);
        Controls.AddRange(new Control[] { ok, cancel });
        AcceptButton = ok; CancelButton = cancel;

        ok.Click += (_, _) =>
        {
            settings.Connections.Add(new SavedConnection
            {
                Name = fields[0].Box.Text,
                SiteUrl = fields[1].Box.Text,
                AuthMode = "AppCertificate",
                TenantId = fields[2].Box.Text,
                AppId = fields[3].Box.Text,
                CertPfxPath = fields[4].Box.Text,
                CertPasswordProtected = fields[5].Box.Text.Length > 0 ? AppSettings.Protect(fields[5].Box.Text) : "",
            });
            settings.Save();
        };
    }
}

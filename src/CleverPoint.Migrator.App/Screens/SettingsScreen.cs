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
        Controls.Add(tabs);
    }

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
        var add = LinkButton("Add app + certificate connection...", 300);
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
        page.Controls.Add(new Label { Text = "Max requests per second (0 = automatic)", AutoSize = true, Location = new Point(20, 58) });
        var rps = new NumericUpDown { Minimum = 0, Maximum = 50, Value = (decimal)Math.Clamp(_settings.MaxRequestsPerSecond, 0, 50), Location = new Point(280, 54), Width = 70 };
        page.Controls.Add(rps);
        page.Controls.Add(new Label
        {
            Text = "Parallel runs share one request budget per tenant, so adding runs\nnever multiplies the load. Throttling pauses are handled for you.",
            AutoSize = true, ForeColor = Brand.TextSecondary, Location = new Point(20, 92),
        });
        AddSave(page, () =>
        {
            _settings.MaxParallelMigrations = (int)parallel.Value;
            _settings.MaxRequestsPerSecond = (double)rps.Value;
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
        var capture = LinkButton("Start capturing diagnostics for an issue report...", 96);
        capture.Click += (_, _) => MessageBox.Show(FindForm(),
            "Diagnostics capture will record a verbose log of everything the app does\nuntil you stop it, then bundle it into a file you can send to support.",
            "Capture diagnostics", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
            fields[i].Box.SetBounds(130, 16 + i * 36, 350, 26);
            Controls.Add(fields[i].Box);
        }
        var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, Location = new Point(310, 240), Width = 80, FlatStyle = FlatStyle.Flat, BackColor = Brand.Accent, ForeColor = Color.White };
        ok.FlatAppearance.BorderSize = 0;
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(398, 240), Width = 80, FlatStyle = FlatStyle.Flat };
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

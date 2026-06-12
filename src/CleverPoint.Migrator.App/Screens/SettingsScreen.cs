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
        var loadPreset = LinkButton("Load template...", 172);
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

        var list = new ListView
        {
            Location = new Point(16, 16),
            Size = new Size(700, 220),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            View = View.Details,
            FullRowSelect = true,
            BorderStyle = BorderStyle.FixedSingle,
        };
        list.Columns.Add("Name", 140);
        list.Columns.Add("Sign-in", 110);
        list.Columns.Add("Site", 200);
        list.Columns.Add("Status", 150);
        list.Columns.Add("Expires", 110);

        void Refresh()
        {
            list.Items.Clear();
            foreach (var c in _settings.Connections)
            {
                var row = new ListViewItem(new[]
                {
                    c.Name,
                    c.AuthMode == "Browser" ? "Browser" : "App + cert",
                    c.SiteUrl,
                    c.LastStatus + (c.LastVerifiedUtc is { } v ? $" ({v.ToLocalTime():g})" : ""),
                    ConnectionTester.ExpiryText(c),
                }) { Tag = c };
                row.SubItems[3].ForeColor = c.LastStatus.StartsWith("Failed") ? Brand.Fail
                    : c.LastStatus.StartsWith("Connected") ? Brand.Ok : Brand.TextSecondary;
                row.UseItemStyleForSubItems = false;
                list.Items.Add(row);
            }
        }
        Refresh();
        page.Controls.Add(list);

        SavedConnection? Selected() => list.SelectedItems.Count > 0 ? list.SelectedItems[0].Tag as SavedConnection : null;

        var buttons = new FlowLayoutPanel { Location = new Point(12, 244), AutoSize = true };
        Button Btn(string text)
        {
            var b = new Button { Text = text, AutoSize = true, Padding = new Padding(10, 3, 10, 3), FlatStyle = FlatStyle.Flat, Margin = new Padding(4, 0, 4, 0) };
            buttons.Controls.Add(b);
            return b;
        }
        var add = Btn("Add connection...");
        var test = Btn("Test");
        var reconnect = Btn("Reconnect...");
        var remove = Btn("Remove");
        page.Controls.Add(buttons);

        add.Click += (_, _) =>
        {
            using var editor = new ConnectionEditor(_settings);
            if (editor.ShowDialog(FindForm()) == DialogResult.OK) Refresh();
        };
        test.Click += async (_, _) =>
        {
            if (Selected() is not { } c) return;
            await ConnectionTester.TestAsync(FindForm(), _settings, c, allowInteractive: true);
            _settings.Save();
            Refresh();
        };
        reconnect.Click += async (_, _) =>
        {
            if (Selected() is not { } c) return;
            await ConnectionTester.ReconnectAsync(FindForm()!, _settings, c);
            _settings.Save();
            Refresh();
        };
        remove.Click += (_, _) =>
        {
            if (Selected() is not { } c) return;
            if (MessageBox.Show(FindForm(), $"Remove the connection '{c.Name}'?", "Remove connection",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            _settings.Connections.Remove(c);
            _settings.Save();
            Refresh();
        };

        page.Controls.Add(new Label
        {
            Text = "Browser sign-in needs no Azure setup: just a name and the site URL.\n" +
                   "App + certificate is for unattended or scheduled copies. Connections\n" +
                   "are checked automatically when the app starts.",
            AutoSize = true, ForeColor = Brand.TextSecondary, Location = new Point(16, 290),
        });
        var provision = LinkButton("Set up a NEW Azure app for me (Global Admin wizard)...", 348);
        provision.Click += (_, _) =>
        {
            using var wizard = new AppRegistrationWizard(_settings);
            if (wizard.ShowDialog(FindForm()) == DialogResult.OK) Refresh();
            Refresh();
        };
        page.Controls.Add(provision);
        return page;
    }

    private TabPage BuildPerformanceTab()
    {
        var page = NewPage("Performance");
        page.Controls.Add(new Label { Text = "Parallel migrations (1-3)", AutoSize = true, Location = new Point(20, 24) });
        var parallel = new NumericUpDown { Minimum = 1, Maximum = 3, Value = Math.Clamp(_settings.MaxParallelMigrations, 1, 3), Location = new Point(220, 20), Width = 70 };
        page.Controls.Add(new Label
        {
            Text = "How many migrations may RUN at the same time. Extra ones queue\n"
                + "and start automatically when a slot frees up. All running migrations\n"
                + "share one polite request budget, so more is not always faster.",
            AutoSize = true, ForeColor = Brand.TextSecondary, Location = new Point(310, 18),
        });
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
            Text = "Self-healing (applies after each migration run):",
            AutoSize = true, Location = new Point(20, 20),
        });
        var autoRetry = new CheckBox
        {
            Text = "Auto re-run incrementals after a run with failures (up to 5 attempts)",
            AutoSize = true, Location = new Point(24, 48), Checked = _settings.SelfHealAutoRetry,
        };
        page.Controls.Add(autoRetry);
        page.Controls.Add(new Label
        {
            Text = "Each pass re-copies only what failed or changed; it stops as soon as a pass is clean.",
            AutoSize = true, ForeColor = Brand.TextSecondary, Location = new Point(42, 70),
        });
        var repair = new CheckBox
        {
            Text = "Detect and re-copy corrupt files (0-byte or truncated on the target)",
            AutoSize = true, Location = new Point(24, 100), Checked = _settings.SelfHealRepairCorrupt,
        };
        page.Controls.Add(repair);
        page.Controls.Add(new Label
        {
            Text = "Only files this tool migrated are ever deleted and replaced.",
            AutoSize = true, ForeColor = Brand.TextSecondary, Location = new Point(42, 122),
        });
        autoRetry.CheckedChanged += (_, _) => { _settings.SelfHealAutoRetry = autoRetry.Checked; _settings.Save(); };
        repair.CheckedChanged += (_, _) => { _settings.SelfHealRepairCorrupt = repair.Checked; _settings.Save(); };

        page.Controls.Add(new Label
        {
            Text = "Per-task knobs (versions to migrate, date filters, user mapping, API package\nsize, large-file threshold) live in the New Migration screen, and can be saved\nas reusable templates there.",
            AutoSize = true, ForeColor = Brand.TextSecondary, Location = new Point(20, 162),
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
    private readonly ComboBox _authMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 380 };
    private readonly Label _testStatus = new() { AutoSize = true, ForeColor = Brand.TextSecondary, MaximumSize = new Size(560, 0) };

    public ConnectionEditor(AppSettings settings)
    {
        Text = "Add connection";
        Size = new Size(640, 470);
        MinimumSize = new Size(620, 440);
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
        // Auth mode decides which fields apply: browser sign-in needs only
        // the name and URL; unattended app+certificate needs everything.
        Controls.Add(new Label { Text = "Authentication", AutoSize = true, Location = new Point(16, 20) });
        _authMode.Items.AddRange(new object[]
        {
            "Browser sign-in - no Azure app needed (recommended)",
            "App + certificate - unattended/scheduled copies",
        });
        _authMode.SelectedIndex = 0;
        _authMode.Location = new Point(130, 16);
        Controls.Add(_authMode);

        for (var i = 0; i < fields.Length; i++)
        {
            Controls.Add(new Label { Text = fields[i].Label, AutoSize = true, Location = new Point(16, 56 + i * 36) });
            fields[i].Box.SetBounds(130, 52 + i * 36, i == 4 ? 300 : 350, 26);
            Controls.Add(fields[i].Box);
        }
        void ApplyMode()
        {
            var cert = _authMode.SelectedIndex == 1;
            for (var i = 2; i < fields.Length; i++) fields[i].Box.Enabled = cert;
        }
        _authMode.SelectedIndexChanged += (_, _) => ApplyMode();
        ApplyMode();

        // PFX path gets a real file browser.
        var browse = new Button { Text = "Browse...", AutoSize = true, FlatStyle = FlatStyle.Flat, Location = new Point(436, 50 + 4 * 36) };
        browse.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = "Certificate files (*.pfx)|*.pfx|All files|*.*" };
            if (dialog.ShowDialog(this) == DialogResult.OK)
                fields[4].Box.Text = dialog.FileName;
        };
        Controls.Add(browse);

        _testStatus.Location = new Point(16, 300);
        Controls.Add(_testStatus);

        // Bottom-right button row in a flow panel: nothing can clip.
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 52, FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 8, 16, 0),
        };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Padding = new Padding(14, 5, 14, 5), FlatStyle = FlatStyle.Flat, Margin = new Padding(10, 0, 0, 0) };
        var ok = new Button { Text = "Test and save", AutoSize = true, Padding = new Padding(14, 5, 14, 5), FlatStyle = FlatStyle.Flat, BackColor = Brand.Accent, ForeColor = Color.White, Margin = new Padding(10, 0, 0, 0) };
        ok.FlatAppearance.BorderSize = 0;
        buttons.Controls.AddRange(new Control[] { cancel, ok });
        Controls.Add(buttons);
        AcceptButton = ok; CancelButton = cancel;

        SavedConnection Build() => new()
        {
            Name = fields[0].Box.Text,
            SiteUrl = fields[1].Box.Text.TrimEnd('/'),
            AuthMode = _authMode.SelectedIndex == 1 ? "AppCertificate" : "Browser",
            TenantId = fields[2].Box.Text,
            AppId = fields[3].Box.Text,
            CertPfxPath = fields[4].Box.Text,
            CertPasswordProtected = fields[5].Box.Text.Length > 0 ? AppSettings.Protect(fields[5].Box.Text) : "",
        };

        ok.Click += async (_, _) =>
        {
            if (fields[0].Box.Text.Trim().Length == 0 || fields[1].Box.Text.Trim().Length == 0)
            {
                _testStatus.Text = "A name and the site URL are needed first.";
                return;
            }
            ok.Enabled = false;
            _testStatus.ForeColor = Brand.TextSecondary;
            _testStatus.Text = _authMode.SelectedIndex == 0
                ? "Opening the sign-in window..."
                : "Testing the connection...";
            var connection = Build();
            var (success, message) = await ConnectionTester.TestAsync(this, settings, connection, allowInteractive: true);
            _testStatus.ForeColor = success ? Brand.Ok : Brand.Fail;
            _testStatus.Text = message;
            ok.Enabled = true;
            if (!success) return;   // stay open so credentials can be fixed

            settings.Connections.Add(connection);
            settings.Save();
            DialogResult = DialogResult.OK;
            Close();
        };
    }
}

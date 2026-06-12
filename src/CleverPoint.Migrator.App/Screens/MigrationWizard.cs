using CleverPoint.Migrator.App.Services;
using CleverPoint.Migrator.App.Theme;
using CleverPoint.Migrator.Core.Auth;
using CleverPoint.Migrator.Core.Csom;
using CleverPoint.Migrator.Core.History;
using CleverPoint.Migrator.Core.MigrationApi;
using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;

namespace CleverPoint.Migrator.App.Screens;

/// <summary>
/// First functional migration flow: source and target (connection + site +
/// list), the engine choice and the core options with safe defaults, then a
/// live run view (filterable log, progress, gentle cancel). Tree drill-down
/// and drag-drop layer on top of this skeleton.
/// </summary>
public class MigrationWizard : Form
{
    private readonly AppSettings _settings;
    private readonly TextBox _sourceSite = new() { Width = 420 };
    private readonly TextBox _sourceList = new() { Width = 250 };
    private readonly TextBox _targetSite = new() { Width = 420 };
    private readonly TextBox _targetList = new() { Width = 250 };
    private readonly ComboBox _engine = new() { Width = 250, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _preserve = new() { Text = "Preserve authors and dates", Checked = true, AutoSize = true };
    private readonly CheckBox _attachments = new() { Text = "Copy attachments", Checked = true, AutoSize = true };
    private readonly CheckBox _permissions = new() { Text = "Copy unique permissions", AutoSize = true };
    private readonly CheckBox _contentOnly = new() { Text = "Schema only (skip content)", AutoSize = true };
    private readonly DataGridView _log = new();
    private readonly ProgressBar _progress = new() { Style = ProgressBarStyle.Marquee, Width = 420, Visible = false };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Brand.TextSecondary };
    private readonly Button _run = new() { Text = "Start migration", Width = 160, Height = 40, BackColor = Brand.Accent, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
    private readonly Button _cancel = new() { Text = "Cancel run", Width = 110, Height = 40, Visible = false, FlatStyle = FlatStyle.Flat };
    private CancellationTokenSource? _cts;

    public MigrationWizard(AppSettings settings)
    {
        _settings = settings;
        Text = "New migration - CleverPoint Migrator";
        Size = new Size(1060, 700);
        MinimumSize = new Size(940, 620);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Brand.Surface;
        Font = Brand.Body;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, Padding = new Padding(16), AutoSize = true };
        layout.Controls.Add(Lbl("Source site URL"), 0, 0); layout.Controls.Add(_sourceSite, 1, 0);
        layout.Controls.Add(Lbl("List / library"), 2, 0); layout.Controls.Add(_sourceList, 3, 0);
        layout.Controls.Add(Lbl("Target site URL"), 0, 1); layout.Controls.Add(_targetSite, 1, 1);
        layout.Controls.Add(Lbl("Target list name"), 2, 1); layout.Controls.Add(_targetList, 3, 1);
        layout.Controls.Add(Lbl("Engine"), 0, 2); layout.Controls.Add(_engine, 1, 2);

        var opts = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        opts.Controls.AddRange(new Control[] { _preserve, _attachments, _permissions, _contentOnly });
        layout.Controls.Add(opts, 1, 3);
        layout.SetColumnSpan(opts, 3);

        var actions = new FlowLayoutPanel { AutoSize = true };
        actions.Controls.AddRange(new Control[] { _run, _cancel, _progress });
        layout.Controls.Add(actions, 1, 4);
        layout.SetColumnSpan(actions, 3);
        layout.Controls.Add(_status, 1, 5);
        layout.SetColumnSpan(_status, 3);

        ConfigureLogGrid();
        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16) };
        host.Controls.Add(_log);
        Controls.Add(host);
        Controls.Add(layout);
        layout.Dock = DockStyle.Top;

        _engine.Items.AddRange(new object[] { "Classic copy (recommended)", "Migration API (Azure blob)" });
        _engine.SelectedIndex = 0;
        _run.FlatAppearance.BorderSize = 0;
        _run.Click += async (_, _) => await RunAsync();
        _cancel.Click += (_, _) => ConfirmCancel();
    }

    private static Label Lbl(string text) => new()
    {
        Text = text, AutoSize = true, ForeColor = Brand.TextPrimary,
        Padding = new Padding(0, 6, 8, 0), Font = Brand.Body,
    };

    private void ConfigureLogGrid()
    {
        _log.Dock = DockStyle.Fill;
        _log.ReadOnly = true;
        _log.AllowUserToAddRows = false;
        _log.RowHeadersVisible = false;
        _log.BackgroundColor = Brand.SurfaceAlt;
        _log.BorderStyle = BorderStyle.None;
        _log.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _log.VirtualMode = false;
        _log.Columns.Add("type", "Type");
        _log.Columns.Add("path", "Item");
        _log.Columns.Add("status", "Status");
        _log.Columns.Add("message", "Detail");
        _log.Columns[0].FillWeight = 12;
        _log.Columns[1].FillWeight = 46;
        _log.Columns[2].FillWeight = 12;
        _log.Columns[3].FillWeight = 30;
    }

    private SpConnection Connect(string siteUrl)
    {
        // App+certificate connections come from saved connections; browser
        // auth flows are wired through the connection manager screen.
        var match = _settings.Connections.FirstOrDefault(c =>
            c.AuthMode == "AppCertificate" && siteUrl.StartsWith(GetHostPrefix(c.SiteUrl), StringComparison.OrdinalIgnoreCase));
        if (match == null)
            throw new InvalidOperationException(
                "No saved app+certificate connection covers this site yet. Add one under Settings > Connections.");
        var creds = new AppCredentials
        {
            TenantId = match.TenantId,
            AppId = match.AppId,
            CertPfxPath = match.CertPfxPath,
            CertPassword = string.IsNullOrEmpty(match.CertPasswordProtected) ? "" : AppSettings.Unprotect(match.CertPasswordProtected),
        };
        return new SpConnection(siteUrl, new CertTokenProvider(creds));
    }

    private static string GetHostPrefix(string url) => new Uri(url).GetLeftPart(UriPartial.Authority);

    private async Task RunAsync()
    {
        if (_sourceSite.Text.Length == 0 || _sourceList.Text.Length == 0 || _targetSite.Text.Length == 0)
        {
            _status.Text = "Fill in the source site, source list and target site first.";
            return;
        }
        var targetTitle = _targetList.Text.Length > 0 ? _targetList.Text : _sourceList.Text;

        _run.Enabled = false;
        _cancel.Visible = true;
        _progress.Visible = true;
        _log.Rows.Clear();
        _cts = new CancellationTokenSource();
        _status.Text = "Running...";

        using var store = new HistoryStore(AppSettings.HistoryDbPath);
        var runId = store.StartRun(new MigrationRun
        {
            Name = $"{_sourceList.Text} -> {targetTitle}",
            SourceUrl = _sourceSite.Text, SourceList = _sourceList.Text,
            TargetUrl = _targetSite.Text, TargetList = targetTitle,
            Engine = _engine.SelectedIndex == 1 ? "MigrationApi" : "Classic",
        });

        var result = new CopyResult();
        result.RecordAdded += rec => BeginInvoke(() =>
        {
            store.RecordItem(runId, rec);
            var row = _log.Rows[_log.Rows.Add(rec.ItemType, rec.SourcePath, rec.Status.ToString(), rec.Message ?? "")];
            row.Cells[2].Style.ForeColor = Brand.StatusColor(rec.Status.ToString());
            if (_log.Rows.Count % 25 == 0) _log.FirstDisplayedScrollingRowIndex = _log.Rows.Count - 1;
        });

        var options = new CopyOptions
        {
            TargetListTitle = targetTitle,
            PreserveAuthorsAndDates = _preserve.Checked,
            CopyAttachments = _attachments.Checked,
            CopyPermissions = _permissions.Checked,
            CopyContent = !_contentOnly.Checked,
        };

        var status = "Completed";
        try
        {
            // Engine work stays off the UI thread; the UI only receives events.
            await Task.Run(async () =>
            {
                var source = Connect(_sourceSite.Text);
                var target = Connect(_targetSite.Text);
                if (_engine.SelectedIndex == 1)
                {
                    var engine = new MigrationApiEngine(source, target);
                    var apiResult = await engine.CopyLibraryAsync(_sourceList.Text, options);
                    foreach (var rec in apiResult.Records)
                        result.Add(rec.ItemType, rec.SourcePath, rec.TargetPath, rec.Status, rec.Message);
                }
                else
                {
                    await CopyEngine.CopyListAsync(source, target, _sourceList.Text, options, null, _cts.Token, result);
                }
            }, _cts.Token);
            if (result.Failed > 0) status = "CompletedWithIssues";
        }
        catch (OperationCanceledException)
        {
            status = "Interrupted";
            _status.Text = "Run cancelled. You can resume it from History.";
        }
        catch (Exception ex)
        {
            status = "Failed";
            _status.Text = $"The run hit a problem: {ex.Message}";
        }
        finally
        {
            store.SaveItemMap(HistoryStore.PairKey(_sourceSite.Text, _sourceList.Text, _targetSite.Text, targetTitle), result.ItemMappings);
            store.FinishRun(runId, result, status);
            _run.Enabled = true;
            _cancel.Visible = false;
            _progress.Visible = false;
            if (status is "Completed" or "CompletedWithIssues")
                _status.Text = $"Done: {result.Summary()}";
        }
    }

    private void ConfirmCancel()
    {
        // Gentle, default-safe confirmation.
        var choice = MessageBox.Show(this,
            "Stop this migration?\n\nEverything copied so far stays in place, and you can resume the run later from History.",
            "Pause for thought", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (choice == DialogResult.Yes)
            _cts?.Cancel();
    }
}

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
    private readonly TextBox _targetUrl = new() { Width = 250 };
    private readonly ComboBox _engine = new() { Width = 250, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _preserve = new() { Text = "Preserve authors and dates", Checked = true, AutoSize = true };
    private readonly CheckBox _attachments = new() { Text = "Copy attachments", Checked = true, AutoSize = true };
    private readonly CheckBox _permissions = new() { Text = "Copy unique permissions", AutoSize = true };
    private readonly CheckBox _contentOnly = new() { Text = "Schema only (skip content)", AutoSize = true };
    private readonly DataGridView _log = new();
    private readonly ProgressBar _progress = new() { Style = ProgressBarStyle.Marquee, Width = 420, Visible = false };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Brand.TextSecondary };
    private readonly Button _run = new() { Text = "Copy structure + content", AutoSize = true, Padding = new Padding(16, 9, 16, 9), BackColor = Brand.Accent, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
    private readonly Button _runContent = new() { Text = "Copy content only", AutoSize = true, Padding = new Padding(16, 9, 16, 9), FlatStyle = FlatStyle.Flat, Margin = new Padding(10, 0, 0, 0) };
    private readonly Button _cancel = new() { Text = "Cancel run", Width = 110, Height = 40, Visible = false, FlatStyle = FlatStyle.Flat };
    private CancellationTokenSource? _cts;

    /// <summary>Prefills the wizard from the explorer (drag-drop or selection).</summary>
    public void Preset(string sourceSite, string sourceList, string targetSite, string targetList,
        string? sourceFolder = null, List<string>? namePatterns = null)
    {
        _sourceSite.Text = sourceSite;
        _sourceList.Text = sourceList;
        _targetSite.Text = targetSite;
        _targetList.Text = targetList;
        _sourceFolderScope = sourceFolder;
        _namePatterns = namePatterns ?? new List<string>();
        var scope = new List<string>();
        if (sourceFolder != null) scope.Add($"folder {sourceFolder.Split('/')[^1]}");
        if (_namePatterns.Count > 0) scope.Add($"only: {string.Join(", ", _namePatterns.Take(5))}{(_namePatterns.Count > 5 ? "..." : "")}");
        _scopeInfo.Text = scope.Count > 0 ? "Scope: " + string.Join("; ", scope) : "";
    }

    private string? _sourceFolderScope;
    private List<string> _namePatterns = new();
    private readonly Label _scopeInfo = new() { AutoSize = true, ForeColor = Brand.TextSecondary };

    public MigrationWizard(AppSettings settings)
    {
        _settings = settings;
        Text = "New migration - CleverPoint Migrator";
        Size = new Size(1060, 700);
        MinimumSize = new Size(940, 620);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Brand.Surface;
        Font = Brand.Body;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, Padding = new Padding(16), AutoSize = true };
        layout.Controls.Add(Lbl("Source site URL"), 0, 0); layout.Controls.Add(_sourceSite, 1, 0);
        layout.Controls.Add(Lbl("List / library"), 2, 0); layout.Controls.Add(_sourceList, 3, 0);
        var browseSource = BrowseButton("source", _sourceSite, _sourceList);
        layout.Controls.Add(browseSource, 4, 0);
        layout.Controls.Add(Lbl("Target site URL"), 0, 1); layout.Controls.Add(_targetSite, 1, 1);
        layout.Controls.Add(Lbl("Target list title"), 2, 1); layout.Controls.Add(_targetList, 3, 1);
        var browseTarget = BrowseButton("target", _targetSite, _targetList);
        layout.Controls.Add(browseTarget, 4, 1);
        // New lists get both a Title and a URL; existing targets ignore the URL.
        layout.Controls.Add(Lbl("Target list URL"), 2, 2); layout.Controls.Add(_targetUrl, 3, 2);
        layout.Controls.Add(Lbl("Engine"), 0, 2); layout.Controls.Add(_engine, 1, 2);
        layout.Controls.Add(_scopeInfo, 1, 3);
        layout.SetColumnSpan(_scopeInfo, 3);

        // Suggest a URL leaf from the title until the user edits the URL.
        var urlTouched = false;
        _targetUrl.TextChanged += (_, _) => { if (_targetUrl.Focused) urlTouched = true; };
        _targetList.TextChanged += (_, _) =>
        {
            if (!urlTouched)
                _targetUrl.Text = string.Concat(_targetList.Text.Where(char.IsLetterOrDigit));
        };

        var opts = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        opts.Controls.AddRange(new Control[] { _preserve, _attachments, _permissions, _contentOnly });
        layout.Controls.Add(opts, 1, 4);
        layout.SetColumnSpan(opts, 3);

        var actions = new FlowLayoutPanel { AutoSize = true };
        actions.Controls.AddRange(new Control[] { _run, _runContent, _cancel, _progress });
        layout.Controls.Add(actions, 1, 5);
        layout.SetColumnSpan(actions, 3);
        layout.Controls.Add(_status, 1, 6);
        layout.SetColumnSpan(_status, 3);

        ConfigureLogGrid();
        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16) };
        host.Controls.Add(_log);
        Controls.Add(host);
        Controls.Add(layout);
        layout.Dock = DockStyle.Top;

        _engine.Items.AddRange(new object[]
        {
            "Classic copy - recommended for most migrations",
            "Migration API - best for very large libraries (1,000+ files)",
        });
        _engine.SelectedIndex = 0;
        _run.FlatAppearance.BorderSize = 0;
        _run.Click += async (_, _) => await RunAsync(contentOnly: false);
        _runContent.Click += async (_, _) => await RunAsync(contentOnly: true);
        _cancel.Click += (_, _) => ConfirmCancel();
    }

    private Button BrowseButton(string role, TextBox site, TextBox list)
    {
        var button = new Button { Text = "Browse...", AutoSize = true, Padding = new Padding(8, 0, 8, 0), FlatStyle = FlatStyle.Flat, Margin = new Padding(8, 2, 0, 0) };
        button.Click += (_, _) =>
        {
            using var picker = new ListPickerDialog(_settings, role, site.Text.Length > 0 ? site.Text : null);
            if (picker.ShowDialog(this) == DialogResult.OK)
            {
                site.Text = picker.SelectedSiteUrl;
                list.Text = picker.SelectedListTitle;
                if (role == "source")
                    SuggestTargetTitle();
            }
        };
        return button;
    }

    /// <summary>
    /// Saves typing: once a source is picked, the target title (and via the
    /// auto-suggest, the URL) follows it. Same-site copies get " - Copy" so the
    /// new list never collides with the source.
    /// </summary>
    private void SuggestTargetTitle()
    {
        if (_sourceList.Text.Length == 0) return;
        var sameSite = string.Equals(_sourceSite.Text.TrimEnd('/'), _targetSite.Text.TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);
        var suggestion = sameSite ? $"{_sourceList.Text} - Copy" : _sourceList.Text;
        if (_targetList.Text.Length == 0 || _targetList.Text == _lastSuggestedTitle)
        {
            _targetList.Text = suggestion;
            _lastSuggestedTitle = suggestion;
        }
    }

    private string? _lastSuggestedTitle;

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

    private SpConnection Connect(string siteUrl) => ConnectionResolver.Resolve(this, _settings, siteUrl);

    private async Task RunAsync(bool contentOnly)
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
        var runStart = DateTime.UtcNow;
        var copiedCount = 0;
        result.RecordAdded += rec => BeginInvoke(() =>
        {
            store.RecordItem(runId, rec);
            var row = _log.Rows[_log.Rows.Add(rec.ItemType, rec.SourcePath, rec.Status.ToString(), rec.Message ?? "")];
            row.Cells[2].Style.ForeColor = Brand.StatusColor(rec.Status.ToString());
            if (_log.Rows.Count % 25 == 0) _log.FirstDisplayedScrollingRowIndex = _log.Rows.Count - 1;

            // Live throughput + rough time remaining once the scan total is known.
            if (rec.Status == ItemCopyStatus.Copied) copiedCount++;
            var elapsed = (DateTime.UtcNow - runStart).TotalSeconds;
            if (elapsed > 5 && copiedCount > 0)
            {
                var rate = copiedCount / elapsed;
                _status.Text = $"Running... {copiedCount} copied, {rate * 60:F0} items/min, {TimeSpan.FromSeconds(elapsed):mm\\:ss} elapsed";
            }
        });

        var options = new CopyOptions
        {
            TargetListTitle = targetTitle,
            TargetListUrl = _targetUrl.Text.Trim().Length > 0 ? _targetUrl.Text.Trim() : null,
            PreserveAuthorsAndDates = _preserve.Checked,
            CopyAttachments = _attachments.Checked,
            CopyPermissions = _permissions.Checked,
            CopyContent = !_contentOnly.Checked,
            // "Copy content only" leaves the target's views and settings alone;
            // fields still merge so item values have somewhere to land.
            CopyViews = !contentOnly,
            CopyListSettings = !contentOnly,
            SourceFolderServerRelativeUrl = _sourceFolderScope,
            NamePatterns = _namePatterns,
        };

        var status = "Completed";
        try
        {
            // Concurrency cap: extra migrations queue and wait their turn.
            RunQueue.Configure(_settings.MaxParallelMigrations);
            if (RunQueue.RunningCount >= _settings.MaxParallelMigrations)
                _status.Text = $"Queued ({RunQueue.RunningCount} running)...";
            using var slot = await RunQueue.EnterAsync();
            _status.Text = "Running...";

            // Connections resolve on the UI thread (browser sign-in may pop);
            // engine work stays off it and the UI only receives events.
            var source = Connect(_sourceSite.Text);
            var target = Connect(_targetSite.Text);
            await Task.Run(async () =>
            {
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
        catch (Exception ex) when (ex.Message.Contains("403") || ex.Message.Contains("401")
            || ex.Message.Contains("Unauthorized") || ex.Message.Contains("Access denied", StringComparison.OrdinalIgnoreCase))
        {
            status = "Failed";
            // A stale browser session is the usual culprit; the next run
            // re-prompts for sign-in.
            ConnectionResolver.InvalidateBrowserSession(_sourceSite.Text);
            ConnectionResolver.InvalidateBrowserSession(_targetSite.Text);
            _status.Text = "Access was denied. Your sign-in session may have expired - " +
                "click Start again to sign in fresh. If it persists, check your permissions on both sites.";
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
            Toasts.Show(status == "Completed" ? "Migration finished" : $"Migration {status}",
                $"{_sourceList.Text} -> {targetTitle}: {result.Summary()}");
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

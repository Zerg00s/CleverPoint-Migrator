using CleverPoint.Migrator.App.Services;
using CleverPoint.Migrator.App.Theme;
using CleverPoint.Migrator.Core.Auth;
using CleverPoint.Migrator.Core.Csom;
using CleverPoint.Migrator.Core.History;
using CleverPoint.Migrator.Core.Http;
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
    // The three action buttons share one size/spacing recipe so they line up.
    private readonly Button _run = new() { Text = "Copy structure + content", AutoSize = true, Padding = new Padding(18, 9, 18, 9), BackColor = Brand.Accent, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 3, 10, 3) };
    private readonly Button _runContent = new() { Text = "Copy content only", AutoSize = true, Padding = new Padding(18, 9, 18, 9), FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 3, 10, 3) };
    private readonly Button _cancel = new() { Text = "Cancel run", AutoSize = true, Padding = new Padding(18, 9, 18, 9), Visible = false, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 3, 10, 3) };
    private readonly Button _export = new() { Text = "Export results (CSV)", AutoSize = true, Padding = new Padding(10, 4, 10, 4), Visible = false, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 3, 10, 3) };
    private readonly Button _compare = new() { Text = "Compare source and target", AutoSize = true, Padding = new Padding(10, 4, 10, 4), Visible = false, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 3, 10, 3) };
    private readonly ComboBox _versions = new() { Width = 180, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 4, 0, 4) };
    private readonly Button _mapping = new() { Text = "User mapping: none", AutoSize = true, Padding = new Padding(10, 4, 10, 4), FlatStyle = FlatStyle.Flat, Margin = new Padding(16, 4, 0, 4) };
    private readonly Button _dateFilter = new() { Text = "Date filter: none", AutoSize = true, Padding = new Padding(10, 4, 10, 4), FlatStyle = FlatStyle.Flat, Margin = new Padding(10, 4, 0, 4) };
    private readonly Button _fieldMapping = new() { Text = "Field mapping: none", AutoSize = true, Padding = new Padding(10, 4, 10, 4), FlatStyle = FlatStyle.Flat, Margin = new Padding(10, 4, 0, 4) };
    private readonly Button _saveTemplate = new() { Text = "Save as template...", AutoSize = true, Padding = new Padding(10, 4, 10, 4), FlatStyle = FlatStyle.Flat, Margin = new Padding(16, 4, 0, 4) };
    private readonly Button _applyTemplate = new() { Text = "Apply template...", AutoSize = true, Padding = new Padding(10, 4, 10, 4), FlatStyle = FlatStyle.Flat, Margin = new Padding(10, 4, 0, 4) };
    private DateTime? _modifiedSinceUtc;
    private DateTime? _modifiedBeforeUtc;
    private long? _lastRunId;
    private CancellationTokenSource? _cts;

    /// <summary>Prefills the wizard from the explorer (drag-drop or selection).</summary>
    public void Preset(string sourceSite, string sourceList, string targetSite, string targetList,
        string? sourceFolder = null, List<string>? selectedPaths = null, List<int>? itemIds = null)
    {
        _sourceSite.Text = sourceSite;
        _sourceList.Text = sourceList;
        _targetSite.Text = targetSite;
        _targetList.Text = targetList;
        _sourceFolderScope = sourceFolder;
        _selectedPaths = selectedPaths ?? new List<string>();
        _itemIds = itemIds ?? new List<int>();
        UpdateScopeInfo();
    }

    private void UpdateScopeInfo(string? prefix = null)
    {
        var scope = new List<string>();
        if (_selectedPaths.Count > 0)
            scope.Add($"{_selectedPaths.Count} selected file(s)/folder(s): "
                + string.Join(", ", _selectedPaths.Take(4).Select(p => p.Split('/')[^1]))
                + (_selectedPaths.Count > 4 ? ", ..." : ""));
        else if (_sourceFolderScope != null)
            scope.Add($"folder {_sourceFolderScope.Split('/')[^1]}");
        if (_itemIds.Count > 0) scope.Add($"{_itemIds.Count} selected item(s)");
        var text = scope.Count > 0 ? "Scope: " + string.Join("; ", scope) : "";
        _scopeInfo.Text = prefix == null ? text : text.Length > 0 ? $"{prefix}  |  {text}" : prefix;
    }

    /// <summary>What a run was scoped to, persisted with the run so re-runs stay scoped.</summary>
    private sealed record ScopePayload(string? Folder, List<string> Paths, List<int> ItemIds);

    private string? _sourceFolderScope;
    private List<string> _selectedPaths = new();
    private List<int> _itemIds = new();
    private DateTime? _deltaBaselineUtc;
    private long? _resumeRunId;

    /// <summary>
    /// Copying INTO an existing target list: structure creation makes no sense
    /// there, so content-only becomes the one primary action.
    /// </summary>
    public void UseContentOnly()
    {
        _run.Visible = false;
        _runContent.BackColor = Brand.Accent;
        _runContent.ForeColor = Color.White;
        _runContent.FlatAppearance.BorderSize = 0;
        _runContent.Text = "Start copy";
    }

    /// <summary>
    /// Reopens a history run in the wizard ("return to session"). Delta mode
    /// pre-arms the incremental baseline; an Interrupted run resumes by
    /// skipping everything its first attempt already copied.
    /// </summary>
    public void PresetFromRun(MigrationRun run, bool delta)
    {
        Preset(run.SourceUrl, run.SourceList, run.TargetUrl, run.TargetList);
        _engine.SelectedIndex = run.Engine == "MigrationApi" ? 1 : 0;

        // Restore the original scope: an incremental over "3 selected files"
        // must stay 3 selected files, not become the whole list.
        if (run.ScopeJson != null)
        {
            try
            {
                var scope = System.Text.Json.JsonSerializer.Deserialize<ScopePayload>(run.ScopeJson);
                if (scope != null)
                {
                    _sourceFolderScope = scope.Folder;
                    _selectedPaths = scope.Paths ?? new List<string>();
                    _itemIds = scope.ItemIds ?? new List<int>();
                }
            }
            catch { /* legacy runs without scope info */ }
        }

        string prefix;
        if (delta)
        {
            _deltaBaselineUtc = run.MaxSourceModifiedUtc;
            prefix = _deltaBaselineUtc != null
                ? $"Incremental: only items changed since {_deltaBaselineUtc.Value.ToLocalTime():g} copy; existing items update in place."
                : "Incremental: existing items update in place (no duplicates); unchanged items re-copy (no baseline from the last run).";
        }
        else if (run.Status == "Interrupted")
        {
            _resumeRunId = run.Id;
            prefix = "Resume: items the interrupted run already copied will be skipped.";
        }
        else
        {
            prefix = "Re-run: existing items update in place (no duplicates).";
        }
        UpdateScopeInfo(prefix);
    }
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
        // Labels and Browse buttons size to content; the input columns share
        // the rest, so nothing clips at any window width.
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        foreach (var input in new Control[] { _sourceSite, _targetSite, _engine, _sourceList, _targetList, _targetUrl })
            input.Anchor = AnchorStyles.Left | AnchorStyles.Right;
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

        // Per-task choices: version depth, identity mapping, advanced date filter.
        _versions.Items.AddRange(new object[]
            { "Latest version only", "Last 5 versions", "Last 10 versions", "All versions (up to 50)" });
        _versions.SelectedIndex = 0;
        var opts2 = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        var versionsLbl = new Label { Text = "File versions:", AutoSize = true, ForeColor = Brand.TextPrimary, Padding = new Padding(0, 6, 6, 0) };
        opts2.Controls.AddRange(new Control[] { versionsLbl, _versions, _mapping, _fieldMapping, _dateFilter, _saveTemplate, _applyTemplate });
        _fieldMapping.Click += (_, _) => EditFieldMapping();
        _saveTemplate.Click += (_, _) => SaveTemplate();
        _applyTemplate.Click += (_, _) => ApplyTemplate();
        layout.Controls.Add(opts2, 1, 5);
        layout.SetColumnSpan(opts2, 3);
        _mapping.Click += (_, _) => PickUserMapping();
        _dateFilter.Click += (_, _) => EditDateFilter();
        _export.Click += (_, _) => ExportResults();

        var actions = new FlowLayoutPanel { AutoSize = true };
        actions.Controls.AddRange(new Control[] { _run, _runContent, _cancel, _export, _compare, _progress });
        _compare.Click += async (_, _) => await RunCompareAsync();
        layout.Controls.Add(actions, 1, 6);
        layout.SetColumnSpan(actions, 3);
        layout.Controls.Add(_status, 1, 7);
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
        // Secondary buttons share one subtle border so nothing looks heavier
        // than its neighbors.
        foreach (var secondary in new[] { _runContent, _cancel, _export, _compare, _mapping, _fieldMapping, _dateFilter, _saveTemplate, _applyTemplate })
        {
            secondary.FlatAppearance.BorderColor = Color.FromArgb(0xC6, 0xCE, 0xD6);
            secondary.FlatAppearance.BorderSize = 1;
        }
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
        GridClipboard.Attach(_log);
        // Double-click any row to open the item in the browser.
        _log.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex < 0) return;
            var path = _log.Rows[e.RowIndex].Cells[1].Value?.ToString();
            if (string.IsNullOrEmpty(path) || !path.StartsWith('/')) return;
            try
            {
                var url = new Uri(new Uri(_sourceSite.Text), path).ToString();
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch { /* no browser association */ }
        };
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
        _runContent.Enabled = false;
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
            ScopeJson = _sourceFolderScope != null || _selectedPaths.Count > 0 || _itemIds.Count > 0
                ? System.Text.Json.JsonSerializer.Serialize(new ScopePayload(_sourceFolderScope, _selectedPaths, _itemIds))
                : null,
        });

        var result = new CopyResult();
        var runStart = DateTime.UtcNow;
        var copiedCount = 0;
        var processed = 0;
        result.RecordAdded += rec => BeginInvoke(() =>
        {
            store.RecordItem(runId, rec);
            var row = _log.Rows[_log.Rows.Add(rec.ItemType, rec.SourcePath, rec.Status.ToString(), rec.Message ?? "")];
            row.Cells[2].Style.ForeColor = Brand.StatusColor(rec.Status.ToString());
            if (_log.Rows.Count % 25 == 0) _log.FirstDisplayedScrollingRowIndex = _log.Rows.Count - 1;

            // Live throughput, percent and ETA once the scan total is known.
            if (rec.Status == ItemCopyStatus.Copied) copiedCount++;
            if (rec.ItemType is "Item" or "File" or "Folder") processed++;
            var elapsed = (DateTime.UtcNow - runStart).TotalSeconds;
            if (elapsed > 5 && copiedCount > 0)
            {
                var rate = copiedCount / elapsed;
                var text = $"Running... {copiedCount} copied, {rate * 60:F0} items/min, {TimeSpan.FromSeconds(elapsed):mm\\:ss} elapsed";
                if (result.PlannedItems > 0 && processed <= result.PlannedItems)
                {
                    var pct = Math.Min(100, processed * 100 / result.PlannedItems);
                    var eta = TimeSpan.FromSeconds((result.PlannedItems - processed) / Math.Max(rate, 0.01));
                    text = $"Running... {pct}% ({processed}/{result.PlannedItems}), {rate * 60:F0} items/min, ETA {(eta.TotalHours >= 1 ? $"{(int)eta.TotalHours}h {eta.Minutes}m" : $"{eta.Minutes}:{eta.Seconds:00}")}";
                    _progress.Style = ProgressBarStyle.Continuous;
                    _progress.Value = pct;
                }
                _status.Text = text;
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
            // "Copy content only" never touches the target schema: no fields,
            // no views, no settings, no formatting. The target must exist.
            MergeSchema = !contentOnly,
            CopyViews = !contentOnly,
            CopyListSettings = !contentOnly,
            SourceFolderServerRelativeUrl = _sourceFolderScope,
            SelectedPaths = _selectedPaths,
            ItemIds = _itemIds,
            MaxVersions = SelectedMaxVersions(),
            FieldMap = new Dictionary<string, string>(_fieldMap, StringComparer.OrdinalIgnoreCase),
            // Delta baseline and the manual date filter compose: the later
            // "since" wins, "before" is the manual filter's alone.
            ModifiedSinceUtc = (_deltaBaselineUtc, _modifiedSinceUtc) switch
            {
                (null, var m) => m,
                (var d, null) => d,
                ({ } d, { } m) => d > m ? d : m,
            },
            ModifiedBeforeUtc = _modifiedBeforeUtc,
        };

        options.UnresolvedUserFallback = _unresolvedFallback;
        Dictionary<string, string>? userMap = null, groupMap = null;
        if (_mappingRows.Count > 0)
        {
            userMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            groupMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (type, src, tgt) in _mappingRows)
                (type == "Group" ? groupMap : userMap)[src] = tgt;
        }

        // Any re-run over a pair we've copied before updates in place instead
        // of duplicating: the persisted source->target item map drives upserts.
        var pairKey = HistoryStore.PairKey(_sourceSite.Text, _sourceList.Text, _targetSite.Text, targetTitle);
        var knownMap = store.GetItemMap(pairKey);
        if (knownMap.Count > 0) options.UpsertItemMap = knownMap;
        if (_resumeRunId is { } rid)
            options.ResumeSkipPaths = store.GetCopiedSourcePaths(rid);

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

            // Content-only copies content INTO an existing list - they never
            // create one. Probe first so the user gets a clear answer instead
            // of a surprise new list.
            if (contentOnly && !await TargetListExistsAsync(target, targetTitle))
                throw new InvalidOperationException(
                    $"the target list '{targetTitle}' does not exist on {_targetSite.Text}. " +
                    "Use 'Copy structure + content' to create it, or pick an existing list.");

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
                    await CopyEngine.CopyListAsync(source, target, _sourceList.Text, options, userMap, _cts.Token, result, groupMap);
                }
            }, _cts.Token);

            // Self-healing (Settings > Advanced, off by default): re-run
            // incrementals until clean and/or re-copy corrupt files.
            if (_engine.SelectedIndex == 0
                && ((result.Failed > 0 && _settings.SelfHealAutoRetry) || _settings.SelfHealRepairCorrupt))
            {
                _status.Text = "Self-healing pass...";
                var healing = new HealingOptions
                {
                    AutoRetry = _settings.SelfHealAutoRetry,
                    RepairCorruptFiles = _settings.SelfHealRepairCorrupt,
                };
                await Task.Run(() => RunCoordinator.HealAsync(source, target, _sourceList.Text, options,
                    healing, result, msg => BeginInvoke(() => _status.Text = msg), _cts.Token), _cts.Token);
            }
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
            store.SaveItemMap(pairKey, result.ItemMappings);
            store.FinishRun(runId, result, status);
            _lastRunId = runId;
            _export.Visible = true;
            _compare.Visible = true;
            _run.Enabled = true;
            _runContent.Enabled = true;
            _cancel.Visible = false;
            _progress.Visible = false;
            if (status is "Completed" or "CompletedWithIssues")
                _status.Text = $"Done: {result.Summary()}";
            Toasts.Show(status == "Completed" ? "Migration finished" : $"Migration {status}",
                $"{_sourceList.Text} -> {targetTitle}: {result.Summary()}");
        }
    }

    private readonly List<(string Type, string Source, string Target)> _mappingRows = new();
    private readonly Dictionary<string, string> _fieldMap = new(StringComparer.OrdinalIgnoreCase);
    private string? _unresolvedFallback;

    /// <summary>Per-task identity mapping: rows, pickers, fallback, CSV in/out.</summary>
    private void PickUserMapping()
    {
        if (_sourceSite.Text.Length == 0 || _targetSite.Text.Length == 0)
        {
            _status.Text = "Fill in the source and target site URLs first - the mapping pickers search those sites.";
            return;
        }
        using var dialog = new UserMappingDialog(_settings, _sourceSite.Text, _targetSite.Text, _mappingRows, _unresolvedFallback);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _mappingRows.Clear();
        _mappingRows.AddRange(dialog.Rows);
        _unresolvedFallback = dialog.UnresolvedFallback;
        var users = _mappingRows.Count(r => r.Type == "User");
        var groups = _mappingRows.Count - users;
        _mapping.Text = _mappingRows.Count == 0 && _unresolvedFallback == null
            ? "User mapping: none"
            : $"User mapping: {users} users, {groups} groups{(_unresolvedFallback != null ? ", fallback set" : "")}";
    }

    /// <summary>Advanced: copy only items modified in a date window.</summary>
    private void EditDateFilter()
    {
        using var dlg = new Form
        {
            Text = "Date filter",
            ClientSize = new Size(380, 190),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
            BackColor = Brand.Surface, Font = Brand.Body,
        };
        var sinceCheck = new CheckBox { Text = "Only items modified on/after", AutoSize = true, Location = new Point(16, 14), Checked = _modifiedSinceUtc != null };
        var sincePick = new DateTimePicker { Location = new Point(36, 38), Width = 200, Format = DateTimePickerFormat.Short, Enabled = sinceCheck.Checked };
        if (_modifiedSinceUtc != null) sincePick.Value = _modifiedSinceUtc.Value.ToLocalTime();
        var beforeCheck = new CheckBox { Text = "Only items modified before", AutoSize = true, Location = new Point(16, 72), Checked = _modifiedBeforeUtc != null };
        var beforePick = new DateTimePicker { Location = new Point(36, 96), Width = 200, Format = DateTimePickerFormat.Short, Enabled = beforeCheck.Checked };
        if (_modifiedBeforeUtc != null) beforePick.Value = _modifiedBeforeUtc.Value.ToLocalTime();
        sinceCheck.CheckedChanged += (_, _) => sincePick.Enabled = sinceCheck.Checked;
        beforeCheck.CheckedChanged += (_, _) => beforePick.Enabled = beforeCheck.Checked;
        var ok = new Button { Text = "Apply", DialogResult = DialogResult.OK, AutoSize = true, Padding = new Padding(14, 4, 14, 4), FlatStyle = FlatStyle.Flat, BackColor = Brand.Accent, ForeColor = Color.White, Location = new Point(196, 140) };
        ok.FlatAppearance.BorderSize = 0;
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Padding = new Padding(14, 4, 14, 4), FlatStyle = FlatStyle.Flat, Location = new Point(288, 140) };
        dlg.Controls.AddRange(new Control[] { sinceCheck, sincePick, beforeCheck, beforePick, ok, cancel });
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        _modifiedSinceUtc = sinceCheck.Checked ? sincePick.Value.Date.ToUniversalTime() : null;
        _modifiedBeforeUtc = beforeCheck.Checked ? beforePick.Value.Date.AddDays(1).ToUniversalTime() : null;
        _dateFilter.Text = (_modifiedSinceUtc, _modifiedBeforeUtc) switch
        {
            (null, null) => "Date filter: none",
            ({ } s, null) => $"Date filter: after {s.ToLocalTime():d}",
            (null, { } b) => $"Date filter: before {b.ToLocalTime():d}",
            ({ } s, { } b) => $"Date filter: {s.ToLocalTime():d} - {b.ToLocalTime():d}",
        };
    }

    /// <summary>Full results of the last run, straight from this screen.</summary>
    private void ExportResults()
    {
        if (_lastRunId is not { } id) return;
        using var dialog = new SaveFileDialog { Filter = "CSV files|*.csv", FileName = $"migration-log-{id}.csv" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        using var store = new HistoryStore(AppSettings.HistoryDbPath);
        store.ExportRunCsv(id, dialog.FileName);
        _status.Text = $"Results exported to {dialog.FileName}";
        OfferToOpen(this, dialog.FileName);
    }

    /// <summary>Every export ends with "open it now?".</summary>
    public static void OfferToOpen(IWin32Window owner, string path)
    {
        if (MessageBox.Show(owner, $"Saved to:\n{path}\n\nOpen it now?", "Export complete",
                MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch { /* no associated app; the file is still there */ }
    }

    /// <summary>Per-task column mapping: source internal name -> target internal name.</summary>
    private void EditFieldMapping()
    {
        using var dlg = new Form
        {
            Text = "Field mapping",
            ClientSize = new Size(560, 360),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
            BackColor = Brand.Surface, Font = Brand.Body,
        };
        var hint = new Label
        {
            Text = "Writes a source column's values into a DIFFERENT target column.\nUse internal column names (Site settings > columns, or the column URL).",
            AutoSize = true, ForeColor = Brand.TextSecondary, Location = new Point(14, 10),
        };
        var src = new TextBox { Location = new Point(14, 58), Width = 180, PlaceholderText = "Source internal name" };
        var tgt = new TextBox { Location = new Point(204, 58), Width = 180, PlaceholderText = "Target internal name" };
        var add = new Button { Text = "Add", AutoSize = true, Location = new Point(394, 56), FlatStyle = FlatStyle.Flat };
        var grid = new ListView { Location = new Point(14, 92), Size = new Size(530, 200), View = View.Details, FullRowSelect = true };
        grid.Columns.Add("Source column", 250);
        grid.Columns.Add("Target column", 250);
        foreach (var (k, v) in _fieldMap) grid.Items.Add(new ListViewItem(new[] { k, v }));
        add.Click += (_, _) =>
        {
            if (src.Text.Trim().Length == 0 || tgt.Text.Trim().Length == 0) return;
            grid.Items.Add(new ListViewItem(new[] { src.Text.Trim(), tgt.Text.Trim() }));
            src.Text = ""; tgt.Text = "";
        };
        var remove = new Button { Text = "Remove selected", AutoSize = true, Location = new Point(14, 300), FlatStyle = FlatStyle.Flat };
        remove.Click += (_, _) => { foreach (ListViewItem i in grid.SelectedItems) grid.Items.Remove(i); };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, Location = new Point(390, 298), FlatStyle = FlatStyle.Flat, BackColor = Brand.Accent, ForeColor = Color.White, Padding = new Padding(14, 4, 14, 4) };
        ok.FlatAppearance.BorderSize = 0;
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Location = new Point(470, 298), FlatStyle = FlatStyle.Flat, Padding = new Padding(10, 4, 10, 4) };
        dlg.Controls.AddRange(new Control[] { hint, src, tgt, add, grid, remove, ok, cancel });
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _fieldMap.Clear();
        foreach (ListViewItem i in grid.Items) _fieldMap[i.SubItems[0].Text] = i.SubItems[1].Text;
        _fieldMapping.Text = _fieldMap.Count == 0 ? "Field mapping: none" : $"Field mapping: {_fieldMap.Count} column(s)";
    }

    /// <summary>After-run validation: field-by-field + sampled content compare.</summary>
    private async Task RunCompareAsync()
    {
        if (_sourceSite.Text.Length == 0 || _sourceList.Text.Length == 0) return;
        _compare.Enabled = false;
        _status.Text = "Comparing source and target...";
        try
        {
            var source = Connect(_sourceSite.Text);
            var target = Connect(_targetSite.Text);
            var targetTitle = _targetList.Text.Length > 0 ? _targetList.Text : _sourceList.Text;
            var report = await Task.Run(() => Core.Validation.CompareReport.RunAsync(
                source, target, _sourceList.Text, targetTitle,
                Array.Empty<string>(), compareContent: true));
            foreach (var m in report.Mismatches.Take(200))
            {
                var row = _log.Rows[_log.Rows.Add("Compare", m, "Warning", "")];
                row.Cells[2].Style.ForeColor = Brand.StatusColor("Warning");
            }
            _status.Text = report.IsClean
                ? $"Compare clean: {report.SourceItems} source vs {report.TargetItems} target items, no mismatches."
                : $"Compare found {report.Mismatches.Count} mismatch(es) - rows added to the log.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Compare failed: {ex.Message}";
        }
        finally
        {
            _compare.Enabled = true;
        }
    }

    private int SelectedMaxVersions() => _versions.SelectedIndex switch { 1 => 5, 2 => 10, 3 => 50, _ => 1 };

    /// <summary>This task's option set, captured for a reusable named template.</summary>
    private CopyOptions TemplateOptions() => new()
    {
        PreserveAuthorsAndDates = _preserve.Checked,
        CopyAttachments = _attachments.Checked,
        CopyPermissions = _permissions.Checked,
        CopyContent = !_contentOnly.Checked,
        MaxVersions = SelectedMaxVersions(),
        ModifiedSinceUtc = _modifiedSinceUtc,
        ModifiedBeforeUtc = _modifiedBeforeUtc,
        UnresolvedUserFallback = _unresolvedFallback,
    };

    private void SaveTemplate()
    {
        using var dialog = new SaveFileDialog { Filter = "Migration template|*.json", FileName = "my-migration-template.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        SettingsPresets.Export(TemplateOptions(), Path.GetFileNameWithoutExtension(dialog.FileName), dialog.FileName);
        _status.Text = $"Template saved: {dialog.FileName}";
    }

    private void ApplyTemplate()
    {
        using var dialog = new OpenFileDialog { Filter = "Migration template|*.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var (name, o) = SettingsPresets.Import(dialog.FileName);
            _preserve.Checked = o.PreserveAuthorsAndDates;
            _attachments.Checked = o.CopyAttachments;
            _permissions.Checked = o.CopyPermissions;
            _contentOnly.Checked = !o.CopyContent;
            _versions.SelectedIndex = o.MaxVersions switch { >= 50 => 3, >= 10 => 2, >= 5 => 1, _ => 0 };
            _modifiedSinceUtc = o.ModifiedSinceUtc;
            _modifiedBeforeUtc = o.ModifiedBeforeUtc;
            _unresolvedFallback = o.UnresolvedUserFallback;
            _dateFilter.Text = (_modifiedSinceUtc, _modifiedBeforeUtc) switch
            {
                (null, null) => "Date filter: none",
                ({ } since, null) => $"Date filter: after {since.ToLocalTime():d}",
                (null, { } before) => $"Date filter: before {before.ToLocalTime():d}",
                ({ } since, { } before) => $"Date filter: {since.ToLocalTime():d} - {before.ToLocalTime():d}",
            };
            _status.Text = $"Template '{name}' applied.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"That file is not a usable template: {ex.Message}", "Apply template",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static async Task<bool> TargetListExistsAsync(SpConnection target, string title)
    {
        try
        {
            using var doc = await target.Rest.GetJsonAsync(
                $"{target.SiteUrl}/_api/web/lists/GetByTitle('{Uri.EscapeDataString(title.Replace("'", "''"))}')?$select=Title");
            return true;
        }
        catch (SpRestException ex) when (ex.StatusCode == 404)
        {
            return false;
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

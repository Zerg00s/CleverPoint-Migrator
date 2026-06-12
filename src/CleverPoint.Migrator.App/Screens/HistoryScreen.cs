using CleverPoint.Migrator.App.Services;
using CleverPoint.Migrator.App.Theme;
using CleverPoint.Migrator.Core.History;

namespace CleverPoint.Migrator.App.Screens;

/// <summary>
/// Migration history: searchable, filterable, sortable; rename runs, export
/// logs, inspect per-item rows filtered by status.
/// </summary>
public class HistoryScreen : UserControl
{
    private readonly DataGridView _grid = new();
    private readonly TextBox _search = new() { Width = 240, PlaceholderText = "Search name, site, list...", Margin = new Padding(0, 2, 12, 0) };
    private readonly MultiSelectFilter _statusFilter = new(
        new[] { "Completed", "CompletedWithIssues", "Interrupted", "Failed", "Running" });
    private List<MigrationRun> _runs = new();

    public HistoryScreen(AppSettings settings, Action<Control> navigate)
    {
        BackColor = Brand.Surface;
        Padding = new Padding(24);

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(0, 6, 0, 0) };
        var export = new Button { Text = "Export log (CSV)", AutoSize = true, Padding = new Padding(10, 2, 10, 2), FlatStyle = FlatStyle.Flat, Margin = new Padding(12, 0, 0, 0) };
        var rename = new Button { Text = "Rename", AutoSize = true, Padding = new Padding(10, 2, 10, 2), FlatStyle = FlatStyle.Flat, Margin = new Padding(12, 0, 0, 0) };
        bar.Controls.AddRange(new Control[] { _search, _statusFilter, rename, export });
        Controls.Add(bar);

        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.RowHeadersVisible = false;
        _grid.BackgroundColor = Brand.SurfaceAlt;
        _grid.BorderStyle = BorderStyle.None;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        foreach (var (name, title) in new[] { ("id", "#"), ("name", "Name"), ("src", "Source"), ("tgt", "Target"),
            ("engine", "Engine"), ("started", "Started"), ("status", "Status"), ("result", "Result") })
        {
            var col = new DataGridViewTextBoxColumn { Name = name, HeaderText = title, SortMode = DataGridViewColumnSortMode.Automatic };
            _grid.Columns.Add(col);
        }
        _grid.Columns["id"].FillWeight = 5;
        Controls.Add(_grid);
        _grid.BringToFront();

        _search.TextChanged += (_, _) => Render();
        _statusFilter.SelectionChanged += Render;
        rename.Click += (_, _) => RenameSelected();
        export.Click += (_, _) => ExportSelected();
        _grid.CellDoubleClick += (_, _) => OpenDetail();

        Reload();
    }

    /// <summary>Per-run detail drawer: every item row, filter chips, clickable links.</summary>
    private void OpenDetail()
    {
        if (SelectedRunId() is not { } id) return;
        using var detail = new RunDetailDialog(id);
        detail.ShowDialog(FindForm());
    }

    private void Reload()
    {
        try
        {
            using var store = new HistoryStore(AppSettings.HistoryDbPath);
            _runs = store.GetRuns(2000);
        }
        catch
        {
            _runs = new List<MigrationRun>();
        }
        Render();
    }

    private void Render()
    {
        var selected = _statusFilter.SelectedValues;
        var query = _search.Text.Trim();
        _grid.Rows.Clear();
        foreach (var run in _runs)
        {
            if (selected.Count > 0 && !selected.Contains(run.Status)) continue;
            if (query.Length > 0
                && !run.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                && !run.SourceUrl.Contains(query, StringComparison.OrdinalIgnoreCase)
                && !run.SourceList.Contains(query, StringComparison.OrdinalIgnoreCase)
                && !run.TargetList.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;

            var row = _grid.Rows[_grid.Rows.Add(run.Id, run.Name, $"{run.SourceUrl} / {run.SourceList}",
                $"{run.TargetUrl} / {run.TargetList}", run.Engine,
                run.StartedUtc.ToLocalTime().ToString("g"), run.Status,
                $"{run.Copied} copied, {run.Skipped} skipped, {run.Warnings} warn, {run.Failed} failed")];
            row.Cells["status"].Style.ForeColor = run.Failed > 0 || run.Status == "Failed" ? Brand.Fail
                : run.Warnings > 0 || run.Status is "CompletedWithIssues" or "Interrupted" ? Brand.Warn : Brand.Ok;
        }
    }

    private long? SelectedRunId() =>
        _grid.SelectedRows.Count > 0 ? Convert.ToInt64(_grid.SelectedRows[0].Cells["id"].Value) : null;

    private void RenameSelected()
    {
        if (SelectedRunId() is not { } id) return;
        var current = _grid.SelectedRows[0].Cells["name"].Value?.ToString() ?? "";
        var input = PromptDialog.Show(FindForm()!, "Rename migration", "New name for this migration:", current);
        if (string.IsNullOrWhiteSpace(input)) return;
        using var store = new HistoryStore(AppSettings.HistoryDbPath);
        store.RenameRun(id, input.Trim());
        Reload();
    }

    private void ExportSelected()
    {
        if (SelectedRunId() is not { } id) return;
        using var dialog = new SaveFileDialog { Filter = "CSV files|*.csv", FileName = $"migration-log-{id}.csv" };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
        using var store = new HistoryStore(AppSettings.HistoryDbPath);
        store.ExportRunCsv(id, dialog.FileName);
    }
}

/// <summary>
/// Per-run item log: filter chips by status, sortable columns, and
/// double-click opens the item in the browser.
/// </summary>
public class RunDetailDialog : Form
{
    private readonly DataGridView _grid = new();
    private readonly List<(string Type, string Source, string Target, string Status, string? Message, string? Url)> _rows;
    private readonly MultiSelectFilter _filter = new(new[] { "Copied", "Skipped", "Warning", "Failed" });

    public RunDetailDialog(long runId)
    {
        Text = $"Migration log - run #{runId}";
        Size = new Size(1040, 640);
        MinimumSize = new Size(820, 480);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Brand.Surface;
        Font = Brand.Body;
        Icon = AppIcon.Create();

        using (var store = new HistoryStore(AppSettings.HistoryDbPath))
        {
            _rows = store.GetItems(runId)
                .Select(i => (i.ItemType, i.SourcePath, i.TargetPath, i.Status, i.Message, i.ItemUrl))
                .ToList();
        }

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(10, 8, 0, 0) };
        bar.Controls.Add(_filter);
        var hint = new Label { Text = "Double-click a row to open the item in your browser", AutoSize = true, ForeColor = Brand.TextSecondary, Margin = new Padding(16, 6, 0, 0) };
        bar.Controls.Add(hint);
        Controls.Add(bar);

        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.RowHeadersVisible = false;
        _grid.BackgroundColor = Brand.SurfaceAlt;
        _grid.BorderStyle = BorderStyle.None;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        foreach (var (name, title, weight) in new[] { ("type", "Type", 10), ("source", "Item", 40), ("status", "Status", 10), ("message", "Detail", 40) })
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = title, FillWeight = weight, SortMode = DataGridViewColumnSortMode.Automatic });
        Controls.Add(_grid);
        _grid.BringToFront();

        _filter.SelectionChanged += Render;
        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex < 0) return;
            var url = _grid.Rows[e.RowIndex].Tag as string;
            if (!string.IsNullOrEmpty(url))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        };
        Render();
    }

    private void Render()
    {
        var selected = _filter.SelectedValues;
        _grid.Rows.Clear();
        foreach (var row in _rows)
        {
            if (selected.Count > 0 && !selected.Contains(row.Status)) continue;
            var gridRow = _grid.Rows[_grid.Rows.Add(row.Type, row.Source, row.Status, row.Message ?? "")];
            gridRow.Cells["status"].Style.ForeColor = Brand.StatusColor(row.Status);
            // The clickable link: explicit item URL when recorded, else the target path.
            gridRow.Tag = row.Url ?? (row.Target.StartsWith("/") ? null : row.Target);
        }
        Text = $"Migration log - {_grid.Rows.Count} of {_rows.Count} rows";
    }
}

/// <summary>
/// A combobox-looking button that drops a checkbox list: multi-select
/// filtering ("Completed, Failed") in one compact control.
/// </summary>
public class MultiSelectFilter : Button
{
    private readonly CheckedListBox _list = new() { CheckOnClick = true, BorderStyle = BorderStyle.None, IntegralHeight = true };
    private readonly ToolStripDropDown _drop = new() { Padding = Padding.Empty };

    public event Action? SelectionChanged;

    public HashSet<string> SelectedValues { get; } = new(StringComparer.OrdinalIgnoreCase);

    public MultiSelectFilter(string[] options)
    {
        AutoSize = true;
        Padding = new Padding(10, 2, 24, 2);
        FlatStyle = FlatStyle.Flat;
        TextAlign = ContentAlignment.MiddleLeft;
        Margin = new Padding(0, 0, 0, 0);
        UpdateLabel();

        _list.Items.AddRange(options);
        _list.Height = options.Length * 22 + 8;
        _list.Width = 220;
        var host = new ToolStripControlHost(_list) { Padding = Padding.Empty, Margin = Padding.Empty };
        _drop.Items.Add(host);

        Click += (_, _) => _drop.Show(this, new Point(0, Height));
        _list.ItemCheck += (_, e) => BeginInvoke(() =>
        {
            SelectedValues.Clear();
            foreach (var item in _list.CheckedItems) SelectedValues.Add(item.ToString()!);
            UpdateLabel();
            SelectionChanged?.Invoke();
        });
    }

    private void UpdateLabel() =>
        Text = SelectedValues.Count == 0 ? "All statuses" : string.Join(", ", SelectedValues);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        // Dropdown chevron.
        var x = Width - 16;
        var y = Height / 2 - 2;
        using var pen = new Pen(Theme.Brand.TextSecondary, 1.6f);
        e.Graphics.DrawLines(pen, new[] { new Point(x, y), new Point(x + 4, y + 4), new Point(x + 8, y) });
    }
}

/// <summary>Small friendly text prompt (no harsh system dialogs).</summary>
public static class PromptDialog
{
    public static string? Show(IWin32Window owner, string title, string label, string initial)
    {
        using var form = new Form
        {
            Text = title,
            Size = new Size(420, 170),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            BackColor = Brand.Surface,
            Font = Brand.Body,
        };
        var prompt = new Label { Text = label, AutoSize = true, Location = new Point(16, 16) };
        var input = new TextBox { Text = initial, Width = 370, Location = new Point(16, 44) };
        var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, Location = new Point(216, 84), AutoSize = true, Padding = new Padding(12, 2, 12, 2), FlatStyle = FlatStyle.Flat, BackColor = Brand.Accent, ForeColor = Color.White };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(308, 84), AutoSize = true, Padding = new Padding(12, 2, 12, 2), FlatStyle = FlatStyle.Flat };
        ok.FlatAppearance.BorderSize = 0;
        form.Controls.AddRange(new Control[] { prompt, input, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form.ShowDialog(owner) == DialogResult.OK ? input.Text : null;
    }
}

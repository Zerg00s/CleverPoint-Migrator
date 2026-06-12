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

        Reload();
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

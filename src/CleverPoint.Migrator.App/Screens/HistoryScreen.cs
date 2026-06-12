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
    private readonly TextBox _search = new() { Width = 240, PlaceholderText = "Search name, site, list..." };
    private readonly ComboBox _statusFilter = new() { Width = 170, DropDownStyle = ComboBoxStyle.DropDownList };
    private List<MigrationRun> _runs = new();

    public HistoryScreen(AppSettings settings, Action<Control> navigate)
    {
        BackColor = Brand.Surface;
        Padding = new Padding(24);

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(0, 6, 0, 0) };
        _statusFilter.Items.AddRange(new object[] { "All statuses", "Completed", "CompletedWithIssues", "Interrupted", "Failed", "Running" });
        _statusFilter.SelectedIndex = 0;
        var export = new Button { Text = "Export log (CSV)", Width = 140, FlatStyle = FlatStyle.Flat };
        var rename = new Button { Text = "Rename", Width = 90, FlatStyle = FlatStyle.Flat };
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
        _statusFilter.SelectedIndexChanged += (_, _) => Render();
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
        var filter = _statusFilter.SelectedIndex <= 0 ? null : _statusFilter.SelectedItem!.ToString();
        var query = _search.Text.Trim();
        _grid.Rows.Clear();
        foreach (var run in _runs)
        {
            if (filter != null && run.Status != filter) continue;
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
        var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, Location = new Point(222, 84), Width = 80, FlatStyle = FlatStyle.Flat, BackColor = Brand.Accent, ForeColor = Color.White };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(308, 84), Width = 80, FlatStyle = FlatStyle.Flat };
        ok.FlatAppearance.BorderSize = 0;
        form.Controls.AddRange(new Control[] { prompt, input, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form.ShowDialog(owner) == DialogResult.OK ? input.Text : null;
    }
}

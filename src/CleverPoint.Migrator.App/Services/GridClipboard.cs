namespace CleverPoint.Migrator.App.Services;

/// <summary>
/// Makes any grid copy-friendly: Ctrl+C copies the selection, and a
/// right-click menu offers the single cell or the whole row(s).
/// </summary>
public static class GridClipboard
{
    public static void Attach(DataGridView grid)
    {
        grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Copy cell", null, (_, _) =>
        {
            var value = grid.CurrentCell?.Value?.ToString();
            if (!string.IsNullOrEmpty(value)) Clipboard.SetText(value);
        });
        menu.Items.Add("Copy row(s)", null, (_, _) =>
        {
            var data = grid.GetClipboardContent();
            if (data != null) Clipboard.SetDataObject(data);
        });
        grid.ContextMenuStrip = menu;
        // Right-click focuses the cell under the cursor so "Copy cell" hits it.
        grid.CellMouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0 && e.ColumnIndex >= 0)
                grid.CurrentCell = grid[e.ColumnIndex, e.RowIndex];
        };
    }
}

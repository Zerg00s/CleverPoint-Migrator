using CleverPoint.Migrator.Core.Csom;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.Core.Operations;

/// <summary>
/// Reads every item, file and folder of a list with its path and whether it has its own permissions.
/// Read-only. The permissions-only copy and the permissions report both start from this.
/// </summary>
public static class UniquePermissionScanner
{
    public sealed record ScannedItem(int Id, string FileRef, bool IsFolder, bool Unique);

    public static async Task ScanAsync(List list, ClientContext context, Action<ScannedItem> onItem, CancellationToken cancel, int pageSize = 500)
    {
        // Paged in the list's own order with no filter, which keeps it clear of the list view threshold.
        var query = new CamlQuery
        {
            ViewXml = "<View Scope='RecursiveAll'><ViewFields><FieldRef Name='ID'/><FieldRef Name='FileRef'/><FieldRef Name='FSObjType'/></ViewFields>" +
                      $"<RowLimit Paged='TRUE'>{pageSize}</RowLimit></View>",
        };
        do
        {
            cancel.ThrowIfCancellationRequested();
            var page = list.GetItems(query);
            context.Load(page, p => p.Include(i => i.Id, i => i.HasUniqueRoleAssignments, i => i.FileSystemObjectType, i => i["FileRef"]),
                p => p.ListItemCollectionPosition);
            await context.ExecuteWithRetryAsync();
            foreach (var item in page)
                onItem(new ScannedItem(item.Id, (string)item["FileRef"], item.FileSystemObjectType == FileSystemObjectType.Folder, item.HasUniqueRoleAssignments));
            query.ListItemCollectionPosition = page.ListItemCollectionPosition;
        } while (query.ListItemCollectionPosition != null);
    }
}

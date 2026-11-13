using CleverPoint.Migrator.Core.Csom;
using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Permissions-only mode, and permissions on a library copy.
///
///   perms-only   copy a library and a list WITHOUT permissions, then give a source folder, file, item
///                and the library itself their own permissions and run a permissions-only job. The
///                target must end up with the same permissions and no other change: nothing copied,
///                assignments the target had by itself replaced, and inheritance restored only when
///                asked to.
///   perms-lib    a normal library copy with "Copy unique permissions" carries folder and file
///                permissions across.
///
/// Needs the provision scenario to have created the test library and list.
/// </summary>
public static class PermissionSyncTests
{
    private const string LibCopy = "MigTest-PermLib";
    private const string LibCopy2 = "MigTest-PermLib2";
    private const string ListCopy = "MigTest-PermList";
    private const string ListCopy2 = "MigTest-PermList2";

    public static async Task PermissionsOnlyAsync()
    {
        var site = await SiteAsync();
        if (site is null) return;
        var userA = TestAssets.SourceUsers[0].Login;
        var userB = TestAssets.SourceUsers[1].Login;

        // ---------- Library: copy without permissions ----------
        using (var ctx = site.CreateContext())
        {
            await ResetSourceAsync(ctx, TestAssets.SourceLibTitle);
            await TestAssets.DeleteIfExistsAsync(ctx, LibCopy);
        }
        var copy = await CopyEngine.CopyListAsync(site, site, TestAssets.SourceLibTitle,
            new CopyOptions { TargetListTitle = LibCopy, TargetListUrl = "MigTestPermLib" });
        Program.Check("perms-only: library copied first", copy.Failed == 0, copy.Summary());

        string folderRef, fileRef, otherFileRef, sourceRoot, targetRoot;
        int targetCountBefore;
        using (var ctx = site.CreateContext())
        {
            var lib = ctx.Web.Lists.GetByTitle(TestAssets.SourceLibTitle);
            var target = ctx.Web.Lists.GetByTitle(LibCopy);
            ctx.Load(lib, l => l.RootFolder.ServerRelativeUrl);
            ctx.Load(target, l => l.RootFolder.ServerRelativeUrl, l => l.ItemCount);
            var items = lib.GetItems(new CamlQuery { ViewXml = "<View Scope='RecursiveAll'><RowLimit>500</RowLimit></View>" });
            ctx.Load(items, p => p.Include(i => i.Id, i => i.FileSystemObjectType, i => i["FileRef"]));
            await ctx.ExecuteWithRetryAsync();
            sourceRoot = lib.RootFolder.ServerRelativeUrl;
            targetRoot = target.RootFolder.ServerRelativeUrl;
            targetCountBefore = target.ItemCount;

            var folder = items.AsEnumerable().FirstOrDefault(i => i.FileSystemObjectType == FileSystemObjectType.Folder);
            var files = items.AsEnumerable().Where(i => i.FileSystemObjectType == FileSystemObjectType.File).ToList();
            if (folder is null || files.Count < 2)
            {
                Program.Check("perms-only: test library has a folder and two files", false, $"{files.Count} files, folder: {folder != null}");
                return;
            }
            folderRef = (string)folder["FileRef"];
            // Files outside that folder, so the folder's permissions do not reach them.
            var loose = files.Where(f => !((string)f["FileRef"]).StartsWith(folderRef + "/", StringComparison.OrdinalIgnoreCase)).ToList();
            if (loose.Count < 2) loose = files;
            fileRef = (string)loose[0]["FileRef"];
            otherFileRef = (string)loose[1]["FileRef"];

            // Source: the folder gives user A Read, the file gives user B Contribute.
            await GrantAsync(ctx, folder, userA, RoleType.Reader);
            await GrantAsync(ctx, loose[0], userB, RoleType.Contributor);

            // Target, set by hand before the run:
            //   - the counterpart of the file already gives user A Contribute (must be replaced);
            //   - another file has its own permissions while its source inherits (must stay unless asked).
            await GrantAsync(ctx, await ItemAtAsync(ctx, Map(fileRef, sourceRoot, targetRoot), false), userA, RoleType.Contributor);
            await GrantAsync(ctx, await ItemAtAsync(ctx, Map(otherFileRef, sourceRoot, targetRoot), false), userA, RoleType.Reader);
        }

        var run = await CopyEngine.CopyListAsync(site, site, TestAssets.SourceLibTitle,
            new CopyOptions { TargetListTitle = LibCopy, PermissionsOnly = true });
        Print(run);
        Program.Check("perms-only: library run has no failures", run.Failed == 0, run.Summary());
        Program.Check("perms-only: one line per object with its own permissions",
            run.Records.Count(r => r.ItemType is "File" or "Folder" && r.Status == ItemCopyStatus.Copied) == 2
            && run.PlannedItems == 2, $"planned {run.PlannedItems}");

        using (var ctx = site.CreateContext())
        {
            var target = ctx.Web.Lists.GetByTitle(LibCopy);
            ctx.Load(target, l => l.ItemCount);
            await ctx.ExecuteWithRetryAsync();
            Program.Check("perms-only: nothing was copied or deleted", target.ItemCount == targetCountBefore, $"{targetCountBefore} -> {target.ItemCount}");

            var folderPerms = await PermissionsAsync(ctx, await ItemAtAsync(ctx, Map(folderRef, sourceRoot, targetRoot), true));
            Program.Check("perms-only: target folder has the source folder's permissions",
                folderPerms.Unique && folderPerms.Has(userA, RoleType.Reader), folderPerms.ToString());

            var filePerms = await PermissionsAsync(ctx, await ItemAtAsync(ctx, Map(fileRef, sourceRoot, targetRoot), false));
            Program.Check("perms-only: target file has the source file's permissions",
                filePerms.Unique && filePerms.Has(userB, RoleType.Contributor), filePerms.ToString());
            Program.Check("perms-only: what the target file had by itself was replaced",
                !filePerms.HasPrincipal(userA), filePerms.ToString());

            var otherPerms = await PermissionsAsync(ctx, await ItemAtAsync(ctx, Map(otherFileRef, sourceRoot, targetRoot), false));
            Program.Check("perms-only: a target-only unique file is left alone by default", otherPerms.Unique, otherPerms.ToString());
        }

        // ---------- Same job, asked to restore inheritance where the source inherits ----------
        var reset = await CopyEngine.CopyListAsync(site, site, TestAssets.SourceLibTitle,
            new CopyOptions { TargetListTitle = LibCopy, PermissionsOnly = true, ResetTargetWhereSourceInherits = true });
        Program.Check("perms-only: reset run has no failures", reset.Failed == 0, reset.Summary());
        using (var ctx = site.CreateContext())
        {
            var otherPerms = await PermissionsAsync(ctx, await ItemAtAsync(ctx, Map(otherFileRef, sourceRoot, targetRoot), false));
            Program.Check("perms-only: inheritance restored where the source inherits", !otherPerms.Unique, otherPerms.ToString());
            var filePerms = await PermissionsAsync(ctx, await ItemAtAsync(ctx, Map(fileRef, sourceRoot, targetRoot), false));
            Program.Check("perms-only: a second run leaves matching permissions in place",
                filePerms.Unique && filePerms.Has(userB, RoleType.Contributor) && !filePerms.HasPrincipal(userA), filePerms.ToString());
        }

        // ---------- The library itself ----------
        using (var ctx = site.CreateContext())
        {
            var lib = ctx.Web.Lists.GetByTitle(TestAssets.SourceLibTitle);
            lib.BreakRoleInheritance(true, false);
            await ctx.ExecuteWithRetryAsync();
            await GrantAsync(ctx, lib, userA, RoleType.Reader);
        }
        var listLevel = await CopyEngine.CopyListAsync(site, site, TestAssets.SourceLibTitle,
            new CopyOptions { TargetListTitle = LibCopy, PermissionsOnly = true });
        Program.Check("perms-only: list-level run has no failures", listLevel.Failed == 0, listLevel.Summary());
        using (var ctx = site.CreateContext())
        {
            var perms = await PermissionsAsync(ctx, ctx.Web.Lists.GetByTitle(LibCopy));
            Program.Check("perms-only: the target library has the source library's permissions",
                perms.Unique && perms.Has(userA, RoleType.Reader), perms.ToString());
        }

        // ---------- A folder selection only touches that folder ----------
        using (var ctx = site.CreateContext())
        {
            // Put user A back on the target file by hand; a job scoped to the folder must not undo that.
            await GrantAsync(ctx, await ItemAtAsync(ctx, Map(fileRef, sourceRoot, targetRoot), false), userA, RoleType.Reader);
        }
        var scoped = await CopyEngine.CopyListAsync(site, site, TestAssets.SourceLibTitle,
            new CopyOptions { TargetListTitle = LibCopy, PermissionsOnly = true, SelectedPaths = { folderRef } });
        Program.Check("perms-only: a folder selection only covers that folder",
            scoped.Failed == 0 && scoped.PlannedItems == 1, $"planned {scoped.PlannedItems}; {scoped.Summary()}");
        using (var ctx = site.CreateContext())
        {
            var filePerms = await PermissionsAsync(ctx, await ItemAtAsync(ctx, Map(fileRef, sourceRoot, targetRoot), false));
            Program.Check("perms-only: files outside the selection keep what they had", filePerms.HasPrincipal(userA), filePerms.ToString());
        }

        // ---------- Generic list: items pair through the copy's item map ----------
        using (var ctx = site.CreateContext())
        {
            await ResetSourceAsync(ctx, TestAssets.SourceListTitle);
            await TestAssets.DeleteIfExistsAsync(ctx, ListCopy);
        }
        var listCopy = await CopyEngine.CopyListAsync(site, site, TestAssets.SourceListTitle,
            new CopyOptions { TargetListTitle = ListCopy, TargetListUrl = "Lists/MigTestPermList" });
        Program.Check("perms-only: list copied first", listCopy.Failed == 0, listCopy.Summary());
        var itemMap = listCopy.ItemMappings.GroupBy(m => m.Item1).ToDictionary(g => g.Key, g => g.Last().Item2);

        int sourceItemId;
        using (var ctx = site.CreateContext())
        {
            var list = ctx.Web.Lists.GetByTitle(TestAssets.SourceListTitle);
            var items = list.GetItems(CamlQuery.CreateAllItemsQuery(20));
            ctx.Load(items, p => p.Include(i => i.Id, i => i.FileSystemObjectType));
            await ctx.ExecuteWithRetryAsync();
            var item = items.AsEnumerable().First(i => i.FileSystemObjectType != FileSystemObjectType.Folder && itemMap.ContainsKey(i.Id));
            sourceItemId = item.Id;
            await GrantAsync(ctx, item, userB, RoleType.Reader);
        }

        var unpaired = await CopyEngine.CopyListAsync(site, site, TestAssets.SourceListTitle,
            new CopyOptions { TargetListTitle = ListCopy, PermissionsOnly = true });
        Program.Check("perms-only: list items with no known pair are skipped, not guessed",
            unpaired.Failed == 0 && unpaired.Records.Any(r => r.ItemType == "Item" && r.Status == ItemCopyStatus.Skipped), unpaired.Summary());

        var paired = await CopyEngine.CopyListAsync(site, site, TestAssets.SourceListTitle,
            new CopyOptions { TargetListTitle = ListCopy, PermissionsOnly = true, UpsertItemMap = itemMap });
        Print(paired);
        Program.Check("perms-only: list run has no failures", paired.Failed == 0, paired.Summary());
        using (var ctx = site.CreateContext())
        {
            var targetItem = ctx.Web.Lists.GetByTitle(ListCopy).GetItemById(itemMap[sourceItemId]);
            var perms = await PermissionsAsync(ctx, targetItem);
            Program.Check("perms-only: the paired list item has the source item's permissions",
                perms.Unique && perms.Has(userB, RoleType.Reader), perms.ToString());
        }

        // ---------- A missing target list is an error, never a create ----------
        var missing = await CopyEngine.CopyListAsync(site, site, TestAssets.SourceListTitle,
            new CopyOptions { TargetListTitle = "MigTest-DoesNotExist", PermissionsOnly = true });
        using (var ctx = site.CreateContext())
        {
            var created = await ListLocator.TryFindAsync(ctx, "MigTest-DoesNotExist");
            Program.Check("perms-only: a missing target list fails and is not created", missing.Failed == 1 && created is null, missing.Summary());
        }

        // Leave the fixtures the way other scenarios expect them.
        using (var ctx = site.CreateContext())
        {
            await ResetSourceAsync(ctx, TestAssets.SourceLibTitle);
            await ResetSourceAsync(ctx, TestAssets.SourceListTitle);
        }
    }

    public static async Task LibraryCopyWithPermissionsAsync()
    {
        var site = await SiteAsync();
        if (site is null) return;
        var userA = TestAssets.SourceUsers[0].Login;
        var userB = TestAssets.SourceUsers[1].Login;

        string folderRef, fileRef, sourceRoot;
        using (var ctx = site.CreateContext())
        {
            await ResetSourceAsync(ctx, TestAssets.SourceLibTitle);
            await TestAssets.DeleteIfExistsAsync(ctx, LibCopy2);
            var lib = ctx.Web.Lists.GetByTitle(TestAssets.SourceLibTitle);
            ctx.Load(lib, l => l.RootFolder.ServerRelativeUrl);
            var items = lib.GetItems(new CamlQuery { ViewXml = "<View Scope='RecursiveAll'><RowLimit>500</RowLimit></View>" });
            ctx.Load(items, p => p.Include(i => i.Id, i => i.FileSystemObjectType, i => i["FileRef"]));
            await ctx.ExecuteWithRetryAsync();
            sourceRoot = lib.RootFolder.ServerRelativeUrl;
            var folder = items.AsEnumerable().First(i => i.FileSystemObjectType == FileSystemObjectType.Folder);
            folderRef = (string)folder["FileRef"];
            var file = items.AsEnumerable().First(i => i.FileSystemObjectType == FileSystemObjectType.File
                && !((string)i["FileRef"]).StartsWith(folderRef + "/", StringComparison.OrdinalIgnoreCase));
            fileRef = (string)file["FileRef"];
            await GrantAsync(ctx, folder, userA, RoleType.Reader);
            await GrantAsync(ctx, file, userB, RoleType.Contributor);
            // The library itself: keep what it inherits and add user B.
            lib.BreakRoleInheritance(true, false);
            await ctx.ExecuteWithRetryAsync();
            await GrantAsync(ctx, lib, userB, RoleType.Reader);
        }

        var result = await CopyEngine.CopyListAsync(site, site, TestAssets.SourceLibTitle,
            new CopyOptions { TargetListTitle = LibCopy2, TargetListUrl = "MigTestPermLib2", CopyPermissions = true });
        Print(result);
        Program.Check("perms-lib: copy has no failures", result.Failed == 0, result.Summary());

        using (var ctx = site.CreateContext())
        {
            var target = ctx.Web.Lists.GetByTitle(LibCopy2);
            ctx.Load(target, l => l.RootFolder.ServerRelativeUrl);
            await ctx.ExecuteWithRetryAsync();
            var targetRoot = target.RootFolder.ServerRelativeUrl;
            var folderPerms = await PermissionsAsync(ctx, await ItemAtAsync(ctx, Map(folderRef, sourceRoot, targetRoot), true));
            Program.Check("perms-lib: folder permissions copied", folderPerms.Unique && folderPerms.Has(userA, RoleType.Reader), folderPerms.ToString());
            var filePerms = await PermissionsAsync(ctx, await ItemAtAsync(ctx, Map(fileRef, sourceRoot, targetRoot), false));
            Program.Check("perms-lib: file permissions copied", filePerms.Unique && filePerms.Has(userB, RoleType.Contributor), filePerms.ToString());
            var libPerms = await PermissionsAsync(ctx, target);
            Program.Check("perms-lib: the library's own permissions copied", libPerms.Unique && libPerms.Has(userB, RoleType.Reader), libPerms.ToString());
            await ResetSourceAsync(ctx, TestAssets.SourceLibTitle);
        }

        // A generic list with its own permissions carries them across as well.
        using (var ctx = site.CreateContext())
        {
            await ResetSourceAsync(ctx, TestAssets.SourceListTitle);
            await TestAssets.DeleteIfExistsAsync(ctx, ListCopy2);
            var list = ctx.Web.Lists.GetByTitle(TestAssets.SourceListTitle);
            list.BreakRoleInheritance(true, false);
            await ctx.ExecuteWithRetryAsync();
            await GrantAsync(ctx, list, userA, RoleType.Contributor);
        }
        var listResult = await CopyEngine.CopyListAsync(site, site, TestAssets.SourceListTitle,
            new CopyOptions { TargetListTitle = ListCopy2, TargetListUrl = "Lists/MigTestPermList2", CopyPermissions = true });
        Program.Check("perms-lib: list copy has no failures", listResult.Failed == 0, listResult.Summary());
        using (var ctx = site.CreateContext())
        {
            var listPerms = await PermissionsAsync(ctx, ctx.Web.Lists.GetByTitle(ListCopy2));
            Program.Check("perms-lib: the list's own permissions copied", listPerms.Unique && listPerms.Has(userA, RoleType.Contributor), listPerms.ToString());
            await ResetSourceAsync(ctx, TestAssets.SourceListTitle);
        }
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static async Task<SpConnection?> SiteAsync()
    {
        var site = Program.TestSite ?? await TestAssets.EnsureTestSiteAsync(Program.Source);
        Program.TestSite = site;
        if (TestAssets.SourceUsers.Count >= 2) return site;
        Program.Check("permission tests need two users on the test site", false, $"{TestAssets.SourceUsers.Count} found");
        return null;
    }

    private static string Map(string sourceRef, string sourceRoot, string targetRoot) => targetRoot + sourceRef[sourceRoot.Length..];

    private static async Task<ListItem> ItemAtAsync(ClientContext ctx, string serverRelativePath, bool isFolder)
    {
        var path = ResourcePath.FromDecodedUrl(serverRelativePath);
        var item = isFolder
            ? ctx.Web.GetFolderByServerRelativePath(path).ListItemAllFields
            : ctx.Web.GetFileByServerRelativePath(path).ListItemAllFields;
        ctx.Load(item, i => i.Id);
        await ctx.ExecuteWithRetryAsync();
        return item;
    }

    /// <summary>Gives the object its own permissions (if it has none yet) and adds one assignment.</summary>
    private static async Task GrantAsync(ClientContext ctx, SecurableObject target, string login, RoleType role)
    {
        var defs = ctx.Web.RoleDefinitions;
        ctx.Load(defs, d => d.Include(x => x.Name, x => x.RoleTypeKind));
        await ctx.ExecuteWithRetryAsync();
        var bindings = new RoleDefinitionBindingCollection(ctx) { defs.AsEnumerable().First(d => d.RoleTypeKind == role) };
        target.BreakRoleInheritance(false, false);
        target.RoleAssignments.Add(ctx.Web.EnsureUser(login), bindings);
        await ctx.ExecuteWithRetryAsync();
    }

    /// <summary>Restores inheritance on a list and everything in it, so each run starts from the same state.</summary>
    private static async Task ResetSourceAsync(ClientContext ctx, string listTitle)
    {
        var list = ctx.Web.Lists.GetByTitle(listTitle);
        var items = list.GetItems(new CamlQuery { ViewXml = "<View Scope='RecursiveAll'><RowLimit>2000</RowLimit></View>" });
        ctx.Load(list, l => l.HasUniqueRoleAssignments);
        ctx.Load(items, p => p.Include(i => i.Id, i => i.HasUniqueRoleAssignments));
        await ctx.ExecuteWithRetryAsync();
        if (list.HasUniqueRoleAssignments) list.ResetRoleInheritance();
        foreach (var item in items.AsEnumerable().Where(i => i.HasUniqueRoleAssignments)) item.ResetRoleInheritance();
        await ctx.ExecuteWithRetryAsync();
    }

    private sealed record Perms(bool Unique, List<(string Login, string Title, List<RoleType> Roles)> Assignments)
    {
        public bool HasPrincipal(string login) => Assignments.Any(a => a.Login.Equals(login, StringComparison.OrdinalIgnoreCase));
        public bool Has(string login, RoleType role) =>
            Assignments.Any(a => a.Login.Equals(login, StringComparison.OrdinalIgnoreCase) && a.Roles.Contains(role));
        public override string ToString() => (Unique ? "unique: " : "inherits: ") +
            string.Join("; ", Assignments.Select(a => $"{a.Title}={string.Join("+", a.Roles)}"));
    }

    private static async Task<Perms> PermissionsAsync(ClientContext ctx, SecurableObject target)
    {
        ctx.Load(target, t => t.HasUniqueRoleAssignments);
        ctx.Load(target.RoleAssignments, ras => ras.Include(
            ra => ra.Member.LoginName, ra => ra.Member.Title, ra => ra.RoleDefinitionBindings.Include(rd => rd.RoleTypeKind)));
        await ctx.ExecuteWithRetryAsync();
        return new Perms(target.HasUniqueRoleAssignments, target.RoleAssignments.AsEnumerable()
            .Select(ra => (ra.Member.LoginName, ra.Member.Title, ra.RoleDefinitionBindings.AsEnumerable().Select(rd => rd.RoleTypeKind).ToList()))
            .ToList());
    }

    private static void Print(CopyResult result)
    {
        foreach (var r in result.Records.Where(r => r.ItemType is "Permission" or "File" or "Folder" or "Item" or "List" or "Progress")
                     .Where(r => r.Message is { Length: > 0 }).Take(25))
            Console.WriteLine($"    [{r.Status}] {r.ItemType} {r.SourcePath.Split('/')[^1]}: {r.Message}");
    }
}

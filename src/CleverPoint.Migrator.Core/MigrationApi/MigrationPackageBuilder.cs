using System.Security;
using System.Globalization;
using System.Text;

namespace CleverPoint.Migrator.Core.MigrationApi;

/// <summary>
/// One file to import. The CONTENT is not held here: the engine streams each
/// file straight into the Azure container (one file in memory at a time) and
/// records only the blob name + size for the manifest. This keeps RAM flat
/// for 10-100K item migrations.
/// </summary>
public class PackageFile
{
    public string LibraryRelativePath { get; set; } = "";   // "Folder-A/doc.bin" or "doc.bin"
    public string BlobName { get; set; } = "";
    public long FileSize { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime ModifiedUtc { get; set; }
    public int AuthorMapId { get; set; } = -1;               // index into Users; -1 = omit
    public int EditorMapId { get; set; } = -1;
    public Dictionary<string, string> TextFields { get; } = new();  // field internal name -> value
}

public class PackageUser
{
    public int MapId { get; set; }
    public string Login { get; set; } = "";   // claims UPN on the TARGET tenant
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
}

/// <summary>A folder with metadata to preserve (optional; folders implied by file paths get defaults).</summary>
public class PackageFolder
{
    public string LibraryRelativePath { get; set; } = "";
    public DateTime? CreatedUtc { get; set; }
    public DateTime? ModifiedUtc { get; set; }
    public int AuthorMapId { get; set; } = -1;
    public int EditorMapId { get; set; } = -1;
}

/// <summary>
/// Builds a minimal-but-valid SPO Migration API import package (PRIME
/// manifest format) for files + folders into an EXISTING document library.
/// The target list/web/root-folder ids must be the real target ids; the
/// Migration API requires explicit identifier consistency.
/// </summary>
public class MigrationPackageBuilder
{
    public Guid TargetWebId { get; set; }
    public string TargetWebUrl { get; set; } = "";       // server-relative, e.g. /sites/X/migtest
    public string TargetSiteUrl { get; set; } = "";      // absolute site URL (ExportSettings)
    public Guid TargetListId { get; set; }
    public string ListUrlLeaf { get; set; } = "";        // e.g. "MigTestApiLib"
    public Guid TargetRootFolderId { get; set; }

    public List<PackageUser> Users { get; } = new();
    public List<PackageFile> Files { get; } = new();
    public List<PackageFolder> Folders { get; } = new();

    /// <summary>
    /// Folder GUIDs shared across ALL chunks of one migration (path -> id,
    /// "" = list root folder). Chunked imports must reference the same folder
    /// ids that earlier jobs created, so the engine owns this map.
    /// </summary>
    public Dictionary<string, Guid> FolderIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Folder paths this chunk DEFINES (SPFolder objects). Folders already
    /// created by an earlier chunk's job are referenced by id only.
    /// </summary>
    public HashSet<string> FoldersToDefine { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>First list item IntId for this chunk; ids must be unique across chunks.</summary>
    public int IntIdStart { get; set; } = 1;

    /// <summary>Returns the manifest package (name -> xml bytes). Content blobs are streamed separately.</summary>
    public Dictionary<string, byte[]> Build()
    {
        var manifest = new StringBuilder();
        manifest.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        manifest.Append("<SPObjects xmlns=\"urn:deployment-manifest-schema\">\n");

        // Folders this chunk defines, shallowest first. Ids come from the
        // shared FolderIds map (pre-assigned by the engine).
        var folderMeta = Folders.ToDictionary(f => f.LibraryRelativePath, f => f, StringComparer.OrdinalIgnoreCase);
        var allDirs = FoldersToDefine
            .OrderBy(d => d.Count(c => c == '/')).ToList();

        // Shapes below mirror an actual AMR (CreateSPAsyncReadJob) export of a
        // real SPO library, captured live 2026-06-11. Notable requirements
        // found the hard way: ListItem needs Name, DocId, DirName,
        // ParentFolderId and Order; SPObject Url is SERVER-relative while
        // File/ListItem urls are web-relative; ModerationStatus is the enum
        // name, not the number.
        var webDir = TargetWebUrl.TrimStart('/');
        var folderIds = FolderIds;
        folderIds[""] = TargetRootFolderId;
        var intId = IntIdStart;
        foreach (var dir in allDirs)
        {
            var id = folderIds[dir];
            var itemId = Guid.NewGuid();
            var parent = ParentDir(dir);
            var name = dir.Contains('/') ? dir[(dir.LastIndexOf('/') + 1)..] : dir;
            var webRelUrl = $"{ListUrlLeaf}/{dir}";
            var serverRelUrl = $"{TargetWebUrl}/{webRelUrl}";
            var dirName = parent.Length == 0 ? $"{webDir}/{ListUrlLeaf}" : $"{webDir}/{ListUrlLeaf}/{parent}";
            var meta = folderMeta.GetValueOrDefault(dir);
            var created = (meta?.CreatedUtc ?? DateTime.UtcNow).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
            var modified = (meta?.ModifiedUtc ?? DateTime.UtcNow).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
            var author = meta is { AuthorMapId: >= 0 } ? $" Author=\"{meta.AuthorMapId}\"" : "";
            var editor = meta is { EditorMapId: >= 0 } ? $" ModifiedBy=\"{meta.EditorMapId}\"" : "";

            manifest.Append($"  <SPObject Id=\"{id}\" ObjectType=\"SPFolder\" ParentId=\"{folderIds[parent]}\" ParentWebId=\"{TargetWebId}\" ParentWebUrl=\"{X(TargetWebUrl)}\" Url=\"{X(serverRelUrl)}\">\n");
            manifest.Append($"    <Folder Id=\"{id}\" Url=\"{X(webRelUrl)}\" Name=\"{X(name)}\" ParentFolderId=\"{folderIds[parent]}\" ParentWebId=\"{TargetWebId}\" ParentWebUrl=\"{X(TargetWebUrl)}\" ContainingDocumentLibrary=\"{TargetListId}\" TimeCreated=\"{created}\" TimeLastModified=\"{modified}\" SortBehavior=\"1\" />\n");
            manifest.Append("  </SPObject>\n");

            manifest.Append($"  <SPObject Id=\"{itemId}\" ObjectType=\"SPListItem\" ParentId=\"{TargetListId}\" ParentWebId=\"{TargetWebId}\" ParentWebUrl=\"{X(TargetWebUrl)}\" Url=\"{X(serverRelUrl)}\">\n");
            manifest.Append($"    <ListItem FileUrl=\"{X(webRelUrl)}\" DocType=\"Folder\" ParentFolderId=\"{folderIds[parent]}\" Order=\"{intId * 100}\" Id=\"{itemId}\" ParentWebId=\"{TargetWebId}\" ParentListId=\"{TargetListId}\" Name=\"{X(name)}\" DirName=\"{X(dirName)}\" IntId=\"{intId}\" DocId=\"{id}\" Version=\"1.0\" ContentTypeId=\"0x0120\"{author}{editor} TimeCreated=\"{created}\" TimeLastModified=\"{modified}\" ModerationStatus=\"Approved\">\n");
            manifest.Append("      <Fields />\n    </ListItem>\n");
            manifest.Append("  </SPObject>\n");
            intId++;
        }
        foreach (var file in Files)
        {
            var fileId = Guid.NewGuid();
            var itemId = Guid.NewGuid();
            var dir = ParentDir(file.LibraryRelativePath);
            var name = file.LibraryRelativePath.Contains('/')
                ? file.LibraryRelativePath[(file.LibraryRelativePath.LastIndexOf('/') + 1)..]
                : file.LibraryRelativePath;
            var webRelUrl = $"{ListUrlLeaf}/{file.LibraryRelativePath}";
            var serverRelUrl = $"{TargetWebUrl}/{webRelUrl}";
            var dirName = dir.Length == 0 ? $"{webDir}/{ListUrlLeaf}" : $"{webDir}/{ListUrlLeaf}/{dir}";
            var dataName = file.BlobName;

            var author = file.AuthorMapId >= 0 ? $" Author=\"{file.AuthorMapId}\"" : "";
            var editor = file.EditorMapId >= 0 ? $" ModifiedBy=\"{file.EditorMapId}\"" : "";
            var created = file.CreatedUtc.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
            var modified = file.ModifiedUtc.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

            manifest.Append($"  <SPObject Id=\"{fileId}\" ObjectType=\"SPFile\" ParentId=\"{folderIds[dir]}\" ParentWebId=\"{TargetWebId}\" ParentWebUrl=\"{X(TargetWebUrl)}\" Url=\"{X(serverRelUrl)}\">\n");
            manifest.Append($"    <File Url=\"{X(webRelUrl)}\" Id=\"{fileId}\" ParentWebId=\"{TargetWebId}\" ParentWebUrl=\"{X(TargetWebUrl)}\" Name=\"{X(name)}\" ListItemIntId=\"{intId}\" ListId=\"{TargetListId}\" ParentId=\"{folderIds[dir]}\" TimeCreated=\"{created}\" TimeLastModified=\"{modified}\" Version=\"1.0\" NoExecute=\"true\" FileSize=\"{file.FileSize}\" Level=\"1\" ContentVersion=\"1\" FileValue=\"{dataName}\"{author}{editor} />\n");
            manifest.Append("  </SPObject>\n");

            manifest.Append($"  <SPObject Id=\"{itemId}\" ObjectType=\"SPListItem\" ParentId=\"{TargetListId}\" ParentWebId=\"{TargetWebId}\" ParentWebUrl=\"{X(TargetWebUrl)}\" Url=\"{X(serverRelUrl)}\">\n");
            manifest.Append($"    <ListItem FileUrl=\"{X(webRelUrl)}\" DocType=\"File\" ParentFolderId=\"{folderIds[dir]}\" Order=\"{intId * 100}\" Id=\"{itemId}\" ParentWebId=\"{TargetWebId}\" ParentListId=\"{TargetListId}\" Name=\"{X(name)}\" DirName=\"{X(dirName)}\" IntId=\"{intId}\" DocId=\"{fileId}\" Version=\"1.0\" ContentTypeId=\"0x0101\"{author}{editor} TimeCreated=\"{created}\" TimeLastModified=\"{modified}\" ModerationStatus=\"Approved\">\n");
            if (file.TextFields.Count > 0)
            {
                manifest.Append("      <Fields>\n");
                foreach (var (fieldName, value) in file.TextFields)
                    manifest.Append($"        <Field Name=\"{X(fieldName)}\" Value=\"{X(value)}\" Type=\"Text\" />\n");
                manifest.Append("      </Fields>\n");
            }
            else
            {
                manifest.Append("      <Fields />\n");
            }
            manifest.Append("    </ListItem>\n");
            manifest.Append("  </SPObject>\n");
            intId++;
        }
        manifest.Append("</SPObjects>\n");

        return new Dictionary<string, byte[]>
        {
            ["Manifest.xml"] = Encoding.UTF8.GetBytes(manifest.ToString()),
            ["ExportSettings.xml"] = Encoding.UTF8.GetBytes(BuildExportSettings()),
            ["SystemData.xml"] = Encoding.UTF8.GetBytes(BuildSystemData()),
            ["UserGroup.xml"] = Encoding.UTF8.GetBytes(BuildUserGroup()),
            ["RootObjectMap.xml"] = Encoding.UTF8.GetBytes(BuildRootObjectMap()),
            ["LookupListMap.xml"] = Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<LookupLists xmlns=\"urn:deployment-lookuplistmap-schema\" />\n"),
            ["Requirements.xml"] = Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Requirements xmlns=\"urn:deployment-requirements-schema\" />\n"),
            ["ViewFormsList.xml"] = Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<ViewFormsList xmlns=\"urn:deployment-viewformlist-schema\" />\n"),
        };
    }

    private string BuildExportSettings() =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
        $"<ExportSettings xmlns=\"urn:deployment-exportsettings-schema\" SiteUrl=\"{X(TargetSiteUrl)}\" FileLocation=\"C:\\Temp\" IncludeSecurity=\"None\" SourceType=\"SharePointOnline\" IgnoreWebParts=\"true\">\n" +
        "  <ExportObjects>\n" +
        $"    <DeploymentObject Id=\"{TargetListId}\" Type=\"List\" ParentId=\"{TargetWebId}\" />\n" +
        "  </ExportObjects>\n" +
        "</ExportSettings>\n";

    private string BuildSystemData() =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
        "<SystemData xmlns=\"urn:deployment-systemdata-schema\">\n" +
        "  <SchemaVersion Version=\"15.0.0.0\" Build=\"16.0.3111.1200\" DatabaseVersion=\"11552\" SiteVersion=\"15\" />\n" +
        "  <ManifestFiles>\n    <ManifestFile Name=\"Manifest.xml\" />\n  </ManifestFiles>\n" +
        "  <SystemObjects>\n" +
        $"    <SystemObject Id=\"{TargetRootFolderId}\" Type=\"Folder\" Url=\"{X($"{TargetWebUrl}/{ListUrlLeaf}".Replace("//", "/"))}\" />\n" +
        $"    <SystemObject Id=\"{TargetWebId}\" Type=\"Web\" Url=\"{X(TargetWebUrl)}\" />\n" +
        $"    <SystemObject Id=\"{TargetListId}\" Type=\"List\" Url=\"{X($"{TargetWebUrl}/{ListUrlLeaf}".Replace("//", "/"))}\" />\n" +
        "  </SystemObjects>\n" +
        "  <RootWebOnlyLists />\n" +
        "</SystemData>\n";

    private string BuildUserGroup()
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<UserGroupMap xmlns=\"urn:deployment-usergroupmap-schema\">\n  <Users>\n");
        foreach (var u in Users)
        {
            // SystemId is REQUIRED by the import schema (verified live: the
            // job dies in DeserializeUserGroupMap without it). For resolvable
            // UPNs SharePoint takes attributes from SiteUsers, so a
            // deterministic synthetic id is sufficient.
            var systemId = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(u.Login));
            sb.Append($"    <User Id=\"{u.MapId}\" Name=\"{X(u.Name)}\" Login=\"{X(u.Login)}\" Email=\"{X(u.Email)}\" IsDomainGroup=\"false\" IsSiteAdmin=\"false\" SystemId=\"{systemId}\" IsDeleted=\"false\" Flags=\"0\" />\n");
        }
        sb.Append("  </Users>\n  <Groups />\n</UserGroupMap>\n");
        return sb.ToString();
    }

    private string BuildRootObjectMap() =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
        "<RootObjects xmlns=\"urn:deployment-rootobjectmap-schema\">\n" +
        $"  <RootObject Id=\"{TargetListId}\" Type=\"List\" ParentId=\"{TargetWebId}\" WebUrl=\"{X(TargetWebUrl)}\" Url=\"{X($"{TargetWebUrl}/{ListUrlLeaf}".Replace("//", "/"))}\" IsDependency=\"false\" />\n" +
        "</RootObjects>\n";

    private static string ParentDir(string path) =>
        path.Contains('/') ? path[..path.LastIndexOf('/')] : "";

    /// <summary>"a/b/c" -> ["a", "a/b", "a/b/c"]</summary>
    private static IEnumerable<string> ExpandPathChain(string dir)
    {
        var parts = dir.Split('/');
        for (var i = 1; i <= parts.Length; i++)
            yield return string.Join('/', parts[..i]);
    }

    private static string X(string s) => SecurityElement.Escape(s) ?? "";
}

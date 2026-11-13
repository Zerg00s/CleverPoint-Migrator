using System.Text;
using System.Text.Json;
using CleverPoint.Migrator.Core.Csom;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Builds the content the report tests check corner cases against: two subsites, a "source" and a
/// "target" that differs from it in known ways. Odd file names, an empty file, uppercase and missing
/// extensions, deep folders, versions and drafts, libraries with every kind of version limit, an
/// empty and a hidden library, list attachments, items with their own permissions.
///
/// Built once and kept: this tenant's retention policy does not let a site be deleted, so a change
/// to the content gets a new number in <see cref="Version"/> and with it new subsites. What was
/// built is written to tools/cli-tests/corners.json, which is what the tests compare against.
/// </summary>
public static class ReportCorners
{
    /// <summary>Raise when the content below changes; the subsites carry it in their address.</summary>
    private const int Version = 1;

    private static string SourceLeaf => $"rptcorners{Version}";
    private static string TargetLeaf => $"rptcorners{Version}b";

    private const string Docs = "Corners Docs";
    private const string LooseSource = "Folder #1 & 50%";
    private const string LooseTarget = "Folder _1 _ 50_";
    private static readonly DateTime Old = new(2021, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A file of the main library. <paramref name="Target"/>: Same, Missing, Empty, Small or Older.</summary>
    private sealed record Doc(string Path, int Size, int Versions = 1, string Target = "Same");

    private static readonly Doc[] Files =
    {
        new("plain.txt", 1000),
        new("empty.txt", 0),
        new("UPPER.PDF", 2048),
        new("noextension", 500),
        new("versions.docx", 3000, Versions: 3),
        new("it's 100% #1 & co (final).txt", 300),
        new("=cmd.txt", 100),
        new("+plus.txt", 101),
        new("-minus.txt", 102),
        new("@at.txt", 103),
        new("a,b;c=d.txt", 200),
        new("ünïcødé 文件.txt", 400),
        new(new string('L', 150) + ".txt", 150),
        new("L1/L2/L3/L4/L5/L6/deep.txt", 600),
        new("L1/Forms/inside-forms.txt", 700),
        new(LooseSource + "/in special.txt", 800),
        new("big.bin", 200_000, Target: "Small"),
        new("zero.txt", 400, Target: "Empty"),
        new("newer.txt", 500, Target: "Older"),
        new("only-source.txt", 900, Target: "Missing"),
        new("Private/secret.txt", 250),
        new("shared.txt", 350),
        new("photo.JPG", 5000),
        new("clip.mp4", 40_000),
        new("mail.msg", 1200),
        new("pack.zip", 7000),
    };

    private static byte[] Bytes(int size, int seed)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    public static async Task RunAsync()
    {
        var parent = Program.Source;
        var (source, sourceNew) = await EnsureWebAsync(parent, SourceLeaf, "Report corners");
        var (target, targetNew) = await EnsureWebAsync(parent, TargetLeaf, "Report corners (target)");
        var uniquePermissions = true;
        if (sourceNew) uniquePermissions = await BuildSourceAsync(source);
        if (targetNew) await BuildTargetAsync(target);
        Program.Check("report-corners: both subsites are there", true, sourceNew || targetNew ? "built" : "already built");

        var manifest = new
        {
            version = Version,
            source = source.SiteUrl,
            target = target.SiteUrl,
            docs = Docs,
            looseSource = LooseSource,
            looseTarget = LooseTarget,
            uniquePermissions,
            files = Files.Select(f => new { path = f.Path, size = f.Size, versions = f.Versions, target = f.Target }).ToList(),
        };
        var path = Path.Combine(FindRepoRoot(), "tools", "cli-tests", "corners.json");
        // Only the addresses differ from one tenant to the next; the file is kept in the repository as it is for the sandbox.
        await System.IO.File.WriteAllTextAsync(path, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n", new UTF8Encoding(false));
        Console.WriteLine($"  wrote {path}");
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir != null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "tools", "cli-tests"))) return dir.FullName;
        throw new DirectoryNotFoundException("Could not find tools/cli-tests above the current folder.");
    }

    private static async Task<(SpConnection Web, bool Created)> EnsureWebAsync(SpConnection parent, string leaf, string title)
    {
        using var ctx = parent.CreateContext();
        ctx.Load(ctx.Web, w => w.ServerRelativeUrl, w => w.Url);
        ctx.Load(ctx.Web.Webs, ws => ws.Include(w => w.ServerRelativeUrl, w => w.Url));
        await ctx.ExecuteQueryAsync();
        var expected = $"{ctx.Web.ServerRelativeUrl}/{leaf}";
        if (ctx.Web.Webs.AsEnumerable().FirstOrDefault(w => w.ServerRelativeUrl.Equals(expected, StringComparison.OrdinalIgnoreCase)) is { } existing)
        {
            Console.WriteLine($"  exists: {existing.Url}");
            return (parent.ForWeb(existing.Url), false);
        }
        var created = ctx.Web.Webs.Add(new WebCreationInformation
        {
            Url = leaf, Title = title, WebTemplate = "STS#3", UseSamePermissionsAsParentSite = true, Language = 1033,
        });
        ctx.Load(created, w => w.Url);
        await ctx.ExecuteQueryAsync();
        Console.WriteLine($"  created: {created.Url}");
        return (parent.ForWeb(created.Url), true);
    }

    private static async Task<List> AddLibraryAsync(ClientContext ctx, string title, string url, Action<List>? configure = null)
    {
        var list = ctx.Web.Lists.Add(new ListCreationInformation { Title = title, TemplateType = (int)ListTemplateType.DocumentLibrary, Url = url });
        list.EnableVersioning = true;
        configure?.Invoke(list);
        list.Update();
        ctx.Load(list.RootFolder, f => f.ServerRelativeUrl);
        await ctx.ExecuteQueryAsync();
        return list;
    }

    /// <summary>Uploads a file, creating the folders above it, and returns it.</summary>
    private static async Task<Microsoft.SharePoint.Client.File> UploadAsync(ClientContext ctx, List library, string path, byte[] content)
    {
        var root = library.RootFolder.ServerRelativeUrl;
        var folder = library.RootFolder;
        var parts = path.Split('/');
        var at = root;
        foreach (var part in parts[..^1])
        {
            at += "/" + part;
            folder = folder.Folders.AddUsingPath(ResourcePath.FromDecodedUrl(at), new FolderCollectionAddParameters { Overwrite = true });
        }
        using var stream = new MemoryStream(content);
        var file = folder.Files.AddUsingPath(ResourcePath.FromDecodedUrl(at + "/" + parts[^1]), new FileCollectionAddParameters { Overwrite = true }, stream);
        ctx.Load(file, f => f.ServerRelativeUrl);
        await ctx.ExecuteQueryAsync();
        return file;
    }

    /// <summary>Gives a file fixed dates, so that the two sides can be told to agree or to differ.</summary>
    private static async Task DateAsync(ClientContext ctx, Microsoft.SharePoint.Client.File file, DateTime when)
    {
        var item = file.ListItemAllFields;
        item["Created"] = when.ToLocalTime();
        item["Modified"] = when.ToLocalTime();
        item.UpdateOverwriteVersion();
        await ctx.ExecuteQueryAsync();
    }

    private static async Task<bool> BuildSourceAsync(SpConnection web)
    {
        using var ctx = web.CreateContext();
        var docs = await AddLibraryAsync(ctx, Docs, "CornersDocs");
        var seed = 0;
        foreach (var doc in Files)
        {
            seed++;
            Microsoft.SharePoint.Client.File? file = null;
            // Earlier versions are smaller, so each version has a size of its own: a third, two thirds, all of it.
            for (var version = 1; version <= doc.Versions; version++)
                file = await UploadAsync(ctx, docs, doc.Path, Bytes(doc.Size * version / doc.Versions, seed * 10 + version));
            // The file that is newer at the source is the one dated later here than on the target.
            if (doc.Versions == 1) await DateAsync(ctx, file!, doc.Target == "Older" ? Old.AddYears(2) : Old);
        }
        docs.RootFolder.Folders.AddUsingPath(ResourcePath.FromDecodedUrl(docs.RootFolder.ServerRelativeUrl + "/Empty folder"), new FolderCollectionAddParameters());
        await ctx.ExecuteQueryAsync();

        var noVersions = await AddLibraryAsync(ctx, "Corners NoVersions", "CornersNoVersions", l => l.EnableVersioning = false);
        await UploadAsync(ctx, noVersions, "a.txt", Bytes(100, 901));
        await UploadAsync(ctx, noVersions, "b.txt", Bytes(200, 902));

        var drafts = await AddLibraryAsync(ctx, "Corners Drafts", "CornersDrafts", l => l.EnableMinorVersions = true);
        await UploadAsync(ctx, drafts, "draft.txt", Bytes(110, 911));
        await UploadAsync(ctx, drafts, "draft.txt", Bytes(120, 912));                 // 0.2, never published
        var published = await UploadAsync(ctx, drafts, "published.txt", Bytes(130, 913));
        published.Publish("first");                                                    // 1.0
        await ctx.ExecuteQueryAsync();
        await UploadAsync(ctx, drafts, "published.txt", Bytes(140, 914));              // 1.1

        var limits = await AddLibraryAsync(ctx, "Corners Limits", "CornersLimits", l =>
        {
            l.MajorVersionLimit = 100;
            l.VersionPolicies.DefaultTrimMode = VersionPolicyTrimMode.ExpireAfter;
            l.VersionPolicies.DefaultExpireAfterDays = 30;
        });
        await UploadAsync(ctx, limits, "limited.txt", Bytes(160, 921));

        await AddLibraryAsync(ctx, "Corners Empty", "CornersEmpty");

        var hidden = await AddLibraryAsync(ctx, "Corners Hidden", "CornersHidden", l => l.Hidden = true);
        await UploadAsync(ctx, hidden, "hidden.txt", Bytes(123, 931));

        await BuildListAsync(ctx, items: 3);

        // A folder and a file with permissions of their own.
        try
        {
            var root = docs.RootFolder.ServerRelativeUrl;
            var reader = ctx.Web.RoleDefinitions.GetByType(RoleType.Reader);
            ctx.Load(ctx.Web.AssociatedVisitorGroup, g => g.Id);
            await ctx.ExecuteQueryAsync();
            foreach (var item in new[]
                     {
                         ctx.Web.GetFolderByServerRelativePath(ResourcePath.FromDecodedUrl(root + "/Private")).ListItemAllFields,
                         ctx.Web.GetFileByServerRelativePath(ResourcePath.FromDecodedUrl(root + "/shared.txt")).ListItemAllFields,
                     })
            {
                item.BreakRoleInheritance(copyRoleAssignments: false, clearSubscopes: false);
                item.RoleAssignments.Add(ctx.Web.AssociatedVisitorGroup, new RoleDefinitionBindingCollection(ctx) { reader });
            }
            await ctx.ExecuteQueryAsync();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  permissions of their own could not be set: {ex.Message.Split('\n')[0]}");
            return false;
        }
    }

    private static async Task BuildListAsync(ClientContext ctx, int items)
    {
        var list = ctx.Web.Lists.Add(new ListCreationInformation { Title = "Corners List", TemplateType = (int)ListTemplateType.GenericList, Url = "Lists/CornersList" });
        list.Fields.AddFieldAsXml("<Field Type='Text' Name='ProjectCode' DisplayName='Project Code' MaxLength='50' />", true, AddFieldOptions.AddFieldInternalNameHint);
        list.Fields.AddFieldAsXml("<Field Type='Number' Name='Budget' DisplayName='Budget' />", true, AddFieldOptions.AddFieldInternalNameHint);
        list.Fields.AddFieldAsXml("<Field Type='Choice' Name='Stage' DisplayName='Stage'><CHOICES><CHOICE>Plan</CHOICE><CHOICE>Build</CHOICE></CHOICES></Field>", true, AddFieldOptions.AddFieldInternalNameHint);
        await ctx.ExecuteQueryAsync();
        for (var i = 1; i <= items; i++)
        {
            var item = list.AddItem(new ListItemCreationInformation());
            item["Title"] = $"Item {i}";
            item["ProjectCode"] = $"P-{i:000}";
            item["Budget"] = i * 1000;
            item["Stage"] = i % 2 == 0 ? "Build" : "Plan";
            item.Update();
            await ctx.ExecuteQueryAsync();
            if (i != 1) continue;
            foreach (var (name, size) in new[] { ("first.txt", 1000), ("second.bin", 2000) })
            {
                using var stream = new MemoryStream(Bytes(size, 950 + size));
                item.AttachmentFiles.AddUsingPath(ResourcePath.FromDecodedUrl(name), stream);
                await ctx.ExecuteQueryAsync();
            }
        }
    }

    /// <summary>The target: the same main library with the planned differences, and a list with more items.</summary>
    private static async Task BuildTargetAsync(SpConnection web)
    {
        using var ctx = web.CreateContext();
        var docs = await AddLibraryAsync(ctx, Docs, "CornersDocs");
        var seed = 0;
        foreach (var doc in Files)
        {
            seed++;
            if (doc.Target == "Missing") continue;
            var size = doc.Target switch { "Empty" => 0, "Small" => 10_000, _ => doc.Size };
            // The folder with characters a migration tool replaces arrives under the replaced name.
            var path = doc.Path.StartsWith(LooseSource + "/", StringComparison.Ordinal) ? LooseTarget + doc.Path[LooseSource.Length..] : doc.Path;
            var file = await UploadAsync(ctx, docs, path, Bytes(size, seed * 10 + doc.Versions));
            await DateAsync(ctx, file, Old);
        }
        var extra = await UploadAsync(ctx, docs, "only-target.txt", Bytes(950, 990));
        await DateAsync(ctx, extra, Old);

        await AddLibraryAsync(ctx, "Corners Empty", "CornersEmpty");
        await BuildListAsync(ctx, items: 5);
    }
}

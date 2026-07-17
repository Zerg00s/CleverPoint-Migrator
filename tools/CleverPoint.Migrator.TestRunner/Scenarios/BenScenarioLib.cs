using CleverPoint.Migrator.Core.Csom;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Provisions the two libraries for a MANUAL UI test of the nested-folder copy fix, matching Ben's report:
///   SOURCE  Ben-Source / Subfolder / Subfolder 2 / Subfolder 3 / (files)
///   TARGET  Ben-Target / TSubfolder / Tsubfolder 2         (where "Subfolder 3" should be dropped)
///
/// Sibling files are seeded at every source level so the test also shows that NOTHING except the picked
/// "Subfolder 3" comes across, and a nested folder inside "Subfolder 3" proves its subtree is preserved.
/// Idempotent-ish: it deletes and recreates both libraries each run. Does not migrate anything.
/// </summary>
public static class BenScenarioLib
{
    private const string Site = "https://gocleverpointcom.sharepoint.com/sites/DemoLargeSite";
    private const string SrcLib = "Ben-Source";
    private const string TgtLib = "Ben-Target";

    public static async Task RunAsync()
    {
        var conn = new SpConnection(Site, new Core.Auth.CertTokenProvider(Program.SourceCreds));
        var bytes = System.Text.Encoding.UTF8.GetBytes("sample content for Ben's nested-folder migration test");

        // ---------- SOURCE ----------
        var srcRoot = await RecreateLibraryAsync(conn, SrcLib);
        foreach (var rel in new[]
        {
            "Subfolder",
            "Subfolder/Subfolder 2",
            "Subfolder/Subfolder 2/Subfolder 3",
            "Subfolder/Subfolder 2/Subfolder 3/Attachments",   // nested subtree under the folder we copy
        })
            await EnsureFolderAsync(conn, srcRoot, rel);

        // Files INSIDE "Subfolder 3" (these should all land in the target) ...
        await AddFileAsync(conn, $"{srcRoot}/Subfolder/Subfolder 2/Subfolder 3", "Report Q1.docx", bytes);
        await AddFileAsync(conn, $"{srcRoot}/Subfolder/Subfolder 2/Subfolder 3", "Budget 2026.xlsx", bytes);
        await AddFileAsync(conn, $"{srcRoot}/Subfolder/Subfolder 2/Subfolder 3", "Notes.txt", bytes);
        await AddFileAsync(conn, $"{srcRoot}/Subfolder/Subfolder 2/Subfolder 3/Attachments", "diagram.png", bytes);

        // ... and SIBLING files at the levels ABOVE it, which must NOT come across.
        await AddFileAsync(conn, srcRoot, "root-level (should NOT copy).txt", bytes);
        await AddFileAsync(conn, $"{srcRoot}/Subfolder", "in Subfolder (should NOT copy).txt", bytes);
        await AddFileAsync(conn, $"{srcRoot}/Subfolder/Subfolder 2", "in Subfolder 2 (should NOT copy).txt", bytes);

        // ---------- TARGET ----------
        var tgtRoot = await RecreateLibraryAsync(conn, TgtLib);
        await EnsureFolderAsync(conn, tgtRoot, "TSubfolder");
        await EnsureFolderAsync(conn, tgtRoot, "TSubfolder/Tsubfolder 2");

        Program.Check("ben-lib: source + target provisioned", true, $"{SrcLib} / {TgtLib}");

        Console.WriteLine();
        Console.WriteLine("  ==================== HOW TO TEST IN THE UI ====================");
        Console.WriteLine($"  Site (both panes): {Site}");
        Console.WriteLine();
        Console.WriteLine("  SOURCE (left pane):");
        Console.WriteLine($"    1. Open the '{SrcLib}' library.");
        Console.WriteLine("    2. Navigate:  Subfolder  ->  Subfolder 2");
        Console.WriteLine("    3. TICK the checkbox on 'Subfolder 3'  (do NOT open it).");
        Console.WriteLine();
        Console.WriteLine("  TARGET (right pane):");
        Console.WriteLine($"    4. Open the '{TgtLib}' library.");
        Console.WriteLine("    5. Navigate:  TSubfolder  ->  Tsubfolder 2   (stop here; this is the destination).");
        Console.WriteLine();
        Console.WriteLine("    6. Click 'Copy to target'  ->  'Begin copy'.");
        Console.WriteLine();
        Console.WriteLine("  EXPECTED (fixed) RESULT:");
        Console.WriteLine("    Ben-Target / TSubfolder / Tsubfolder 2 / Subfolder 3 / Report Q1.docx, Budget 2026.xlsx,");
        Console.WriteLine("                                                             Notes.txt, Attachments/diagram.png");
        Console.WriteLine("    - 'Subfolder 3' sits DIRECTLY under 'Tsubfolder 2' (no 'Subfolder' / 'Subfolder 2' rebuilt).");
        Console.WriteLine("    - none of the 'should NOT copy' sibling files appear.");
        Console.WriteLine("  (The OLD bug would have created Tsubfolder 2 / Subfolder / Subfolder 2 / Subfolder 3 / ...)");
        Console.WriteLine("  ===============================================================");
    }

    private static async Task<string> RecreateLibraryAsync(SpConnection conn, string title)
    {
        using var ctx = conn.CreateContext();
        await TestAssets.DeleteIfExistsAsync(ctx, title);
        var lib = ctx.Web.Lists.Add(new ListCreationInformation
        {
            Title = title, TemplateType = (int)ListTemplateType.DocumentLibrary, Url = title.Replace(" ", ""),
        });
        ctx.Load(lib, l => l.RootFolder.ServerRelativeUrl);
        await ctx.ExecuteWithRetryAsync();
        Console.WriteLine($"  created '{title}'");
        return lib.RootFolder.ServerRelativeUrl;
    }

    private static async Task EnsureFolderAsync(SpConnection conn, string root, string rel)
    {
        using var ctx = conn.CreateContext();
        var parent = rel.Contains('/') ? $"{root}/{rel[..rel.LastIndexOf('/')]}" : root;
        try
        {
            ctx.Web.GetFolderByServerRelativeUrl(parent).Folders.Add($"{root}/{rel}");
            await ctx.ExecuteWithRetryAsync();
        }
        catch (ServerException) { /* already there */ }
    }

    private static async Task AddFileAsync(SpConnection conn, string folderUrl, string name, byte[] bytes)
    {
        using var ctx = conn.CreateContext();
        ctx.Web.GetFolderByServerRelativeUrl(folderUrl)
            .Files.Add(new FileCreationInformation { Url = name, Content = bytes, Overwrite = true });
        await ctx.ExecuteWithRetryAsync();
    }
}

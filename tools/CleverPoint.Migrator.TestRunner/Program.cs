using CleverPoint.Migrator.Core.Auth;
using CleverPoint.Migrator.Core.Csom;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner;

/// <summary>
/// Live integration test runner for the CleverPoint Migrator engine.
/// Runs named scenarios against real tenants using app-only cert auth.
/// Usage: dotnet run -- [scenario ...]   (default: all)
/// </summary>
public static class Program
{
    public static SpConnection Source = null!;
    public static SpConnection Target = null!;
    public static SpConnection? TestSite;

    // Exposed so scenarios can spin up extra connections (other site collections,
    // e.g. /sites/LMAS) using the same app-only cert credentials.
    public static AppCredentials SourceCreds = null!;
    public static AppCredentials TargetCreds = null!;

    private static readonly List<(string Name, string Detail, bool Pass, string? Error)> Results = new();

    public static async Task<int> Main(string[] args)
    {
        var repoRoot = FindRepoRoot();
        var sourceCreds = AppCredentials.LoadFromFile(Path.Combine(repoRoot, "Secrets", "Source Tenant - Migrator App.txt"));
        var targetCreds = AppCredentials.LoadFromFile(Path.Combine(repoRoot, "Secrets", "Target Tenant - Migrator App.txt"));
        SourceCreds = sourceCreds;
        TargetCreds = targetCreds;

        Source = new SpConnection("https://gocleverpointcom.sharepoint.com/sites/DemoLargeSite", new CertTokenProvider(sourceCreds));
        Target = new SpConnection("https://cleverpointlab.sharepoint.com", new CertTokenProvider(targetCreds));

        Source.Rest.OnThrottle += (url, wait, attempt) =>
            Console.WriteLine($"  [throttle] {wait}s wait (attempt {attempt}) on {url}");

        var scenarios = new Dictionary<string, Func<Task>>(StringComparer.OrdinalIgnoreCase)
        {
            ["auth"] = Scenarios.AuthSmokeTest.RunAsync,
            ["provision"] = Scenarios.SameSiteCopyTests.ProvisionAsync,
            ["copy-list"] = Scenarios.SameSiteCopyTests.CopyListAsync,
            ["copy-selected"] = Scenarios.SameSiteCopyTests.CopySelectedItemsAsync,
            ["copy-paths"] = Scenarios.SameSiteCopyTests.CopySelectedPathsAsync,
            ["browse-large"] = Scenarios.BrowseLargeTest.RunAsync,
            ["content-only"] = Scenarios.ContentOnlyTest.RunAsync,
            ["count-probe"] = Scenarios.CountProbe.RunAsync,
            ["meta-fallback"] = Scenarios.DocMetaFallbackTest.RunAsync,
            ["newdoc-lab"] = Scenarios.NewDocLab.RunAsync,
            ["license-probe"] = Scenarios.LicenseProbe.RunAsync,
            ["folder-users"] = Scenarios.FolderUserCheck.RunAsync,
            ["date-lab"] = Scenarios.DateWriteLab.RunAsync,
            ["copy-lib"] = Scenarios.SameSiteCopyTests.CopyLibraryAsync,
            ["inspect"] = Scenarios.InspectScenario.RunAsync,
            ["copy-cross"] = Scenarios.CrossTenantCopyTests.RunAsync,
            ["folder-lab"] = Scenarios.FolderMetaLab.RunAsync,
            ["copy-crosssite"] = Scenarios.CrossSiteCopyTest.RunAsync,
            ["copy-api"] = Scenarios.MigrationApiTest.RunAsync,
            ["amr-lab"] = Scenarios.AmrExportLab.RunAsync,
            ["scale-provision"] = Scenarios.ScaleTests.ProvisionBigLibraryAsync,
            ["scale-api"] = Scenarios.ScaleTests.ScaleApiCopyAsync,
            ["scale-list"] = Scenarios.ScaleTests.ScaleListCopyAsync,
            ["bigfile"] = Scenarios.BigFileTest.RunAsync,
            ["delta"] = Scenarios.DeltaResumeTests.DeltaAsync,
            ["resume"] = Scenarios.DeltaResumeTests.ResumeAsync,
            ["ctypes"] = Scenarios.ContentTypeTests.RunAsync,
            ["perms"] = Scenarios.PermissionTests.RunAsync,
            ["usermap"] = Scenarios.UserMappingTests.RunAsync,
            ["xt-api"] = Scenarios.CrossTenantApiTest.RunAsync,
            ["compare"] = Scenarios.CompareReportTest.RunAsync,
            ["filters"] = Scenarios.FilterThrottleTests.FiltersAsync,
            ["throttle"] = Scenarios.FilterThrottleTests.ThrottleAsync,
            ["healing"] = Scenarios.HealingTests.RunAsync,
            ["pages"] = Scenarios.PageTests.RunAsync,
            ["chars"] = Scenarios.SpecialCharTests.RunAsync,
            ["versions"] = Scenarios.VersionTests.RunAsync,
            ["bench"] = Scenarios.BenchmarkTests.RunAsync,
            ["browse-lmas"] = Scenarios.BatchAndBrowseTests.BrowseLargeLibFixAsync,
            ["browse-nav"] = Scenarios.BatchAndBrowseTests.FolderNavAsync,
            ["batch"] = Scenarios.BatchAndBrowseTests.BatchSameTenantAsync,
            ["batch-cross"] = Scenarios.BatchAndBrowseTests.BatchCrossTenantAsync,
            ["seed-history"] = Scenarios.SeedHistoryTests.RunAsync,
            ["page-group"] = Scenarios.PageGroupTest.RunAsync,
            ["page-dump"] = Scenarios.PageDumpTest.RunAsync,
            ["copy-cross-meta"] = Scenarios.CrossTenantMetaTest.RunAsync,
            ["date-filter"] = Scenarios.DateFilterTest.RunAsync,
            ["quotes-probe"] = Scenarios.QuotesProbe.RunAsync,
            ["onenote-probe"] = Scenarios.OneNoteProbe.RunAsync,
            ["onenote-copy"] = Scenarios.OneNoteCopyTest.RunAsync,
            ["subfolder-copy"] = Scenarios.SubfolderCopyTest.RunAsync,
            ["structure-only"] = Scenarios.StructureOnlyTest.RunAsync,
            ["chunk-boundary"] = Scenarios.ChunkBoundaryTest.RunAsync,
            ["existing-mode-new"] = Scenarios.ExistingModeNewTest.RunAsync,
            ["api-job-fail"] = Scenarios.ApiJobFailTest.RunAsync,
            ["api-emit-logic"] = Scenarios.ApiEmitLogicTest.RunAsync,
            ["date-folder"] = Scenarios.DateFolderTest.RunAsync,
            ["folder-restamp"] = Scenarios.FolderRestampTest.RunAsync,
            ["content-meta-warn"] = Scenarios.ContentMetaWarnTest.RunAsync,
            ["batch-partial"] = Scenarios.BatchPartialTest.RunAsync,
            ["folder-field"] = Scenarios.FolderFieldTest.RunAsync,
            ["fast-select"] = Scenarios.FastSelectTest.RunAsync,
            ["locale-probe"] = Scenarios.LocaleProbe.RunAsync,
            ["locale-folder"] = Scenarios.LocaleFolderTest.RunAsync,
            ["throughput"] = Scenarios.ThroughputTest.RunAsync,
            ["sysaccount"] = Scenarios.SystemAccountMapTest.RunAsync,
            ["principals"] = Scenarios.PrincipalFetchTest.RunAsync,
            ["taxprobe"] = Scenarios.TaxonomyProbe.RunAsync,
            ["taxcopy"] = Scenarios.TaxonomyCopyTest.RunAsync,
            ["parallel"] = Scenarios.ParallelCopyTest.RunAsync,
            ["pf-race"] = Scenarios.ParallelFolderRaceTest.RunAsync,
            ["update-version"] = Scenarios.UpdateVersionTest.RunAsync,
        };

        var toRun = args.Length > 0 ? args : scenarios.Keys.ToArray();
        foreach (var name in toRun)
        {
            if (!scenarios.TryGetValue(name, out var run))
            {
                Console.WriteLine($"Unknown scenario '{name}'. Available: {string.Join(", ", scenarios.Keys)}");
                return 2;
            }
            Console.WriteLine();
            Console.WriteLine($"=== SCENARIO: {name} ===");
            try
            {
                await run();
            }
            catch (Exception ex)
            {
                Check(name + " (scenario crashed)", false, ex.ToString());
            }
        }

        Console.WriteLine();
        Console.WriteLine("==================== RESULTS ====================");
        var failed = 0;
        foreach (var r in Results)
        {
            Console.WriteLine($"  [{(r.Pass ? "PASS" : "FAIL")}] {r.Name}{(r.Detail.Length > 0 ? " - " + r.Detail : "")}");
            if (!r.Pass)
            {
                failed++;
                if (r.Error != null) Console.WriteLine($"         {r.Error}");
            }
        }
        Console.WriteLine($"  {Results.Count - failed}/{Results.Count} passed");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>Records a test assertion. Returns the condition so callers can branch.</summary>
    public static bool Check(string name, bool condition, string? detailOrError = null)
    {
        Results.Add((name, condition ? (detailOrError ?? "") : "", condition, condition ? null : detailOrError));
        Console.WriteLine($"  [{(condition ? "PASS" : "FAIL")}] {name}{(detailOrError != null ? " - " + detailOrError : "")}");
        return condition;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "Secrets"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the repo root (folder containing 'Secrets').");
    }
}

using CleverPoint.Migrator.Core.Model;

namespace CleverPoint.Migrator.Core.Hosting;

/// <summary>Source scope persisted with a run (as JSON in MigrationRun.ScopeJson)
/// so the run can be re-opened in the explorer at the exact folder + selection.</summary>
public class RunScope
{
    public string? SourceFolder { get; set; }
    public string? TargetListUrl { get; set; }
    // Root folder paths of the lists, so "Run again" finds them whatever language their titles are shown in.
    public string? SourceListPath { get; set; }
    public string? TargetListPath { get; set; }
    public string? TargetSubfolder { get; set; }
    public List<string> SelectedPaths { get; set; } = new();
    public List<int> ItemIds { get; set; } = new();
    // Added so "Run again" reconstructs the copy faithfully (older runs default these to
    // false; RunDetail derives sensible fallbacks from the selection when absent).
    public bool IsLibrary { get; set; }
    public bool ContentOnly { get; set; }

    // Wizard settings, so "Run again" / "Copy task" reopen the wizard with the SAME choices
    // (engine, existing-item mode, date filter, versions, checkboxes) instead of the app
    // defaults. Nullable: runs recorded before these existed fall back to the defaults.
    public string? Engine { get; set; }
    public string? CopyMode { get; set; }        // full | content | structure | permissions
    public int? MaxVersions { get; set; }
    public string? ExistingMode { get; set; }    // Overwrite | CopyIfNewer | Skip
    public string? DateField { get; set; }       // Modified | Created
    public DateTime? ModifiedSinceUtc { get; set; }
    public DateTime? ModifiedBeforeUtc { get; set; }
    public bool? PreserveAuthorsAndDates { get; set; }
    public bool? CopyAttachments { get; set; }
    public bool? CopyPermissions { get; set; }
    public bool? ResetTargetWhereSourceInherits { get; set; }

    /// <summary>
    /// Everything the run was started with, so it can be repeated exactly. The per-run working sets
    /// (resume skip list, item pairs, term map) are left out: they are rebuilt when a run starts.
    /// </summary>
    public CopyOptions? Options { get; set; }

    /// <summary>Build the settings part of a scope from the options the copy actually ran with.</summary>
    public static RunScope FromOptions(CopyOptions o, string engine, bool isLibrary) => new()
    {
        SourceFolder = o.SourceFolderServerRelativeUrl,
        TargetListUrl = o.TargetListUrl,
        SourceListPath = o.SourceListServerRelativeUrl,
        TargetListPath = o.TargetListServerRelativeUrl,
        TargetSubfolder = o.TargetSubfolderRelative,
        SelectedPaths = o.SelectedPaths,
        ItemIds = o.ItemIds,
        IsLibrary = isLibrary,
        ContentOnly = !o.MergeSchema,
        Engine = engine,
        CopyMode = o.PermissionsOnly ? "permissions" : !o.CopyContent ? "structure" : !o.MergeSchema ? "content" : "full",
        MaxVersions = o.MaxVersions,
        ExistingMode = o.ExistingMode.ToString(),
        DateField = o.DateField.ToString(),
        ModifiedSinceUtc = o.ModifiedSinceUtc,
        ModifiedBeforeUtc = o.ModifiedBeforeUtc,
        PreserveAuthorsAndDates = o.PreserveAuthorsAndDates,
        CopyAttachments = o.CopyAttachments,
        CopyPermissions = o.CopyPermissions,
        ResetTargetWhereSourceInherits = o.ResetTargetWhereSourceInherits,
        Options = Portable(o),
    };

    private static CopyOptions Portable(CopyOptions o)
    {
        var copy = o.Clone();
        copy.ResumeSkipPaths = null;
        copy.UpsertItemMap = null;
        copy.TermMap = null;
        return copy;
    }
}

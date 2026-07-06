using CleverPoint.Migrator.Core.Updates;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Pure version-compare for the "update available" check (installer). No network: exercises the
/// tag parsing + IsNewer decision across the shapes GitHub release tags come in.
/// </summary>
public static class UpdateVersionTest
{
    public static Task RunAsync()
    {
        Program.Check("update: newer patch is an update", UpdateVersion.IsNewer("1.0.5", "v1.0.6"), "1.0.5 -> v1.0.6");
        Program.Check("update: newer minor is an update", UpdateVersion.IsNewer("1.0.9", "v1.1.0"), "1.0.9 -> v1.1.0");
        Program.Check("update: same version is NOT an update", !UpdateVersion.IsNewer("1.0.6", "v1.0.6"), "equal");
        Program.Check("update: older tag is NOT an update", !UpdateVersion.IsNewer("1.2.0", "v1.1.9"), "older");
        Program.Check("update: tag without 'v' works", UpdateVersion.IsNewer("1.0.0", "1.0.1"), "no v prefix");
        Program.Check("update: 1.2 == 1.2.0 (component normalize)", !UpdateVersion.IsNewer("1.2.0", "v1.2"), "1.2 vs 1.2.0");
        Program.Check("update: prerelease suffix stripped", !UpdateVersion.IsNewer("1.0.6", "v1.0.6-beta"), "1.0.6-beta");
        Program.Check("update: prerelease of a newer build still newer", UpdateVersion.IsNewer("1.0.6", "v1.0.7-rc1"), "1.0.7-rc1");
        Program.Check("update: malformed tag is safe (not newer)", !UpdateVersion.IsNewer("1.0.6", "latest"), "garbage");
        Program.Check("update: null/empty is safe", !UpdateVersion.IsNewer("1.0.6", null) && !UpdateVersion.IsNewer(null, "v2.0.0"), "nulls");
        Program.Check("update: Parse normalizes 1.2 -> 1.2.0.0", UpdateVersion.Parse("v1.2")?.ToString() == "1.2.0.0",
            UpdateVersion.Parse("v1.2")?.ToString() ?? "null");
        return Task.CompletedTask;
    }
}

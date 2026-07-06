namespace CleverPoint.Migrator.Core.Updates;

/// <summary>
/// Pure version comparison for the "update available" check: parses a GitHub release tag
/// ("v1.2.3", "1.2.3", "v1.2.3-beta") and decides whether it is newer than the running build.
/// Kept free of HTTP so it can be unit-tested directly.
/// </summary>
public static class UpdateVersion
{
    /// <summary>Parses a tag/version string to a Version, or null if it isn't a valid X.Y[.Z].</summary>
    public static Version? Parse(string? tagOrVersion)
    {
        if (string.IsNullOrWhiteSpace(tagOrVersion)) return null;
        var s = tagOrVersion.Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s[1..];
        // Drop a prerelease/build suffix ("-beta", "+meta") before parsing.
        var cut = s.IndexOfAny(new[] { '-', '+' });
        if (cut >= 0) s = s[..cut];
        return Version.TryParse(s, out var v) ? Normalize(v) : null;
    }

    // Version.TryParse leaves unspecified components as -1; normalize to 0 so 1.2 == 1.2.0.
    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    /// <summary>True when <paramref name="latestTag"/> is a strictly newer release than the
    /// running <paramref name="currentVersion"/>. Unparseable input is treated as "not newer".</summary>
    public static bool IsNewer(string? currentVersion, string? latestTag)
    {
        var cur = Parse(currentVersion);
        var latest = Parse(latestTag);
        return cur != null && latest != null && latest > cur;
    }
}

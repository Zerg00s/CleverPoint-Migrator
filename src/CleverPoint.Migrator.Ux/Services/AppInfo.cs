using System.Reflection;

namespace CleverPoint.Migrator.Ux.Services;

/// <summary>
/// Which version this is. A release is stamped with its number when it is published (Sign-App.ps1).
/// A build from source carries the number of the last release it comes after, and says that it is a
/// development build, so the corner of the window never shows a number that was never released.
/// </summary>
public static class AppInfo
{
    private static readonly Assembly App = typeof(AppInfo).Assembly;

    /// <summary>The version number: "1.0.18".</summary>
    public static string Version { get; } =
        App.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";

    /// <summary>Built from source rather than published as a release.</summary>
    public static bool IsDevBuild { get; } =
        App.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Contains("-dev", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>The version as shown: "1.0.18", or "1.0.18 dev" for a build from source.</summary>
    public static string Label => IsDevBuild ? Version + " dev" : Version;

    public static string Explanation => IsDevBuild
        ? $"A development build, made from source after release {Version}."
        : $"Release {Version}.";
}

namespace CleverPoint.Migrator.Ux.Services;

/// <summary>Values written the way a person says them.</summary>
public static class Friendly
{
    /// <summary>A size in the unit that reads best: "812 KB", "4.2 MB", "133.4 GB".</summary>
    public static string Bytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes:N0} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:N0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1048576.0:N1} MB";
        if (bytes < 1024L * 1024 * 1024 * 1024) return $"{bytes / 1073741824.0:N1} GB";
        return $"{bytes / 1099511627776.0:N2} TB";
    }

    /// <summary>
    /// A length of time with its units: "13 s", "3 min 58 s", "1 h 19 min", "2 d 4 h". Two units at
    /// most: past an hour the seconds no longer matter.
    /// </summary>
    public static string Duration(TimeSpan time)
    {
        if (time < TimeSpan.Zero) time = TimeSpan.Zero;
        if (time.TotalSeconds < 1) return "under 1 s";
        if (time.TotalMinutes < 1) return $"{time.Seconds} s";
        if (time.TotalHours < 1) return time.Seconds == 0 ? $"{time.Minutes} min" : $"{time.Minutes} min {time.Seconds} s";
        if (time.TotalDays < 1) return time.Minutes == 0 ? $"{time.Hours} h" : $"{time.Hours} h {time.Minutes} min";
        return time.Hours == 0 ? $"{(int)time.TotalDays} d" : $"{(int)time.TotalDays} d {time.Hours} h";
    }
}

namespace CleverPoint.Migrator.Core.Model;

/// <summary>
/// Pure throughput math for the live progress UI: items/sec, bytes/sec and an ETA
/// from processed/total/elapsed. Kept free of UI so it can be unit-tested directly.
/// </summary>
public static class Throughput
{
    public readonly record struct Stats(double ItemsPerSecond, double BytesPerSecond, TimeSpan? Eta);

    /// <summary>
    /// Rates are averaged over the whole elapsed window. ETA is remaining items divided by
    /// the item rate, and is null whenever it can't be trusted: total unknown/zero, nothing
    /// processed yet, elapsed at zero, or already complete.
    /// </summary>
    public static Stats Estimate(int processed, int total, long bytes, TimeSpan elapsed)
    {
        var secs = elapsed.TotalSeconds;
        if (secs <= 0 || processed <= 0)
            return new Stats(0, 0, null);

        var itemsPerSec = processed / secs;
        var bytesPerSec = bytes / secs;

        TimeSpan? eta = null;
        if (total > processed && itemsPerSec > 0)
        {
            var remaining = total - processed;
            var etaSecs = remaining / itemsPerSec;
            // Guard against absurd values from a stalled early sample.
            if (double.IsFinite(etaSecs) && etaSecs >= 0 && etaSecs < TimeSpan.MaxValue.TotalSeconds)
                eta = TimeSpan.FromSeconds(etaSecs);
        }
        return new Stats(itemsPerSec, bytesPerSec, eta);
    }

    /// <summary>Human "1.2 MB/s" / "930 KB/s" for a bytes-per-second rate.</summary>
    public static string FormatRate(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0) return "0 B/s";
        string[] units = { "B/s", "KB/s", "MB/s", "GB/s" };
        var i = 0;
        var v = bytesPerSecond;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return v >= 100 ? $"{v:0} {units[i]}" : $"{v:0.0} {units[i]}";
    }

    /// <summary>Compact ETA: "2h 05m", "4m 12s", "38s".</summary>
    public static string FormatEta(TimeSpan eta)
    {
        if (eta.TotalHours >= 1) return $"{(int)eta.TotalHours}h {eta.Minutes:00}m";
        if (eta.TotalMinutes >= 1) return $"{eta.Minutes}m {eta.Seconds:00}s";
        return $"{eta.Seconds}s";
    }
}

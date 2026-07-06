using CleverPoint.Migrator.Core.Model;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Pure-math test for the live throughput / ETA read-out (F6). No network: it exercises
/// Throughput.Estimate and the formatters across the edge cases the Activity view relies on
/// (unknown total, zero elapsed, nothing processed yet, mid-run, complete).
/// </summary>
public static class ThroughputTest
{
    public static Task RunAsync()
    {
        // Mid-run: 50 of 200 items in 10s, 100 MB moved -> 5 items/s, 10 MB/s, 30s left.
        var mid = Throughput.Estimate(50, 200, 100L * 1024 * 1024, TimeSpan.FromSeconds(10));
        Program.Check("throughput: item rate", Math.Abs(mid.ItemsPerSecond - 5.0) < 0.001, $"{mid.ItemsPerSecond}");
        Program.Check("throughput: byte rate ~10MB/s", Math.Abs(mid.BytesPerSecond - 10.0 * 1024 * 1024) < 1, $"{mid.BytesPerSecond}");
        Program.Check("throughput: ETA = 30s (150 left / 5 per s)",
            mid.Eta.HasValue && Math.Abs(mid.Eta.Value.TotalSeconds - 30.0) < 0.001, $"{mid.Eta}");

        // Zero elapsed -> no rates, no ETA (avoids divide-by-zero blow-ups).
        var zero = Throughput.Estimate(10, 100, 1000, TimeSpan.Zero);
        Program.Check("throughput: zero elapsed is safe",
            zero.ItemsPerSecond == 0 && zero.BytesPerSecond == 0 && zero.Eta is null, $"{zero}");

        // Nothing processed yet -> no ETA (can't extrapolate from 0).
        var none = Throughput.Estimate(0, 100, 0, TimeSpan.FromSeconds(5));
        Program.Check("throughput: no items yet -> no ETA", none.Eta is null, $"{none}");

        // Unknown total (scan not finished) -> rate known, ETA null.
        var unknownTotal = Throughput.Estimate(40, 0, 4096, TimeSpan.FromSeconds(8));
        Program.Check("throughput: unknown total -> rate but no ETA",
            unknownTotal.ItemsPerSecond > 0 && unknownTotal.Eta is null, $"{unknownTotal}");

        // Already complete (processed >= total) -> no ETA.
        var done = Throughput.Estimate(100, 100, 999, TimeSpan.FromSeconds(20));
        Program.Check("throughput: complete -> no ETA", done.Eta is null, $"{done}");

        // Formatters.
        Program.Check("throughput: FormatRate MB", Throughput.FormatRate(10.0 * 1024 * 1024) == "10.0 MB/s",
            Throughput.FormatRate(10.0 * 1024 * 1024));
        Program.Check("throughput: FormatRate KB", Throughput.FormatRate(1536) == "1.5 KB/s",
            Throughput.FormatRate(1536));
        Program.Check("throughput: FormatRate zero", Throughput.FormatRate(0) == "0 B/s",
            Throughput.FormatRate(0));
        Program.Check("throughput: FormatEta hours", Throughput.FormatEta(TimeSpan.FromMinutes(125)) == "2h 05m",
            Throughput.FormatEta(TimeSpan.FromMinutes(125)));
        Program.Check("throughput: FormatEta minutes", Throughput.FormatEta(TimeSpan.FromSeconds(252)) == "4m 12s",
            Throughput.FormatEta(TimeSpan.FromSeconds(252)));
        Program.Check("throughput: FormatEta seconds", Throughput.FormatEta(TimeSpan.FromSeconds(38)) == "38s",
            Throughput.FormatEta(TimeSpan.FromSeconds(38)));

        return Task.CompletedTask;
    }
}

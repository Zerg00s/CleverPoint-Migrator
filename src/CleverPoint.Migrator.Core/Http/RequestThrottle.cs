using System.Collections.Concurrent;

namespace CleverPoint.Migrator.Core.Http;

/// <summary>
/// Process-wide request pacing, shared per tenant host: when 2-3 migrations
/// run in parallel they draw from ONE budget instead of multiplying load
/// (the ShareGate-style "how many requests to send" control). A 429 anywhere
/// pauses everyone talking to that host for the server-requested delay.
/// </summary>
public static class RequestThrottle
{
    private class HostState
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public double MinIntervalMs;
        public DateTime NextAllowedUtc = DateTime.MinValue;
        public DateTime PausedUntilUtc = DateTime.MinValue;
    }

    private static readonly ConcurrentDictionary<string, HostState> Hosts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Set the budget for a host. 0 = unlimited (default).</summary>
    public static void Configure(string host, double maxRequestsPerSecond)
    {
        var state = Hosts.GetOrAdd(host, _ => new HostState());
        state.MinIntervalMs = maxRequestsPerSecond <= 0 ? 0 : 1000.0 / maxRequestsPerSecond;
    }

    /// <summary>Every outbound request awaits its turn here.</summary>
    public static async Task WaitTurnAsync(string host)
    {
        if (!Hosts.TryGetValue(host, out var state)) return;

        TimeSpan delay;
        await state.Gate.WaitAsync();
        try
        {
            var now = DateTime.UtcNow;
            var earliest = state.PausedUntilUtc > state.NextAllowedUtc ? state.PausedUntilUtc : state.NextAllowedUtc;
            if (earliest < now) earliest = now;
            delay = earliest - now;
            state.NextAllowedUtc = earliest.AddMilliseconds(state.MinIntervalMs);
        }
        finally
        {
            state.Gate.Release();
        }
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay);
    }

    /// <summary>Server pushed back (429/503): pause ALL traffic to the host.</summary>
    public static void PauseHost(string host, TimeSpan duration)
    {
        var state = Hosts.GetOrAdd(host, _ => new HostState());
        var until = DateTime.UtcNow + duration;
        if (until > state.PausedUntilUtc) state.PausedUntilUtc = until;
    }
}

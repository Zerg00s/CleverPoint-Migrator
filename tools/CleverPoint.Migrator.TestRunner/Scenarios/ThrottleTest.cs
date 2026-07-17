using System.Net;
using CleverPoint.Migrator.Core.Csom;
using CleverPoint.Migrator.Core.Http;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Deterministic proof that a 429 no longer fails an item. Two mechanisms:
///   1. detection -- ExecuteWithRetryAsync retries only what it recognizes as throttling, so it MUST
///      recognize the exact exception SharePoint/CSOM throws ("The remote server returned an error: (429)",
///      the ServerException in the user's screenshot), and honor Retry-After when present;
///   2. the shared per-host gate -- a 429 anywhere pauses all traffic to that host for the requested delay,
///      so concurrent workers back off instead of hammering a throttled tenant.
///
/// The tenant will not reliably throttle a test on demand, so this exercises the logic directly.
/// </summary>
public static class ThrottleTest
{
    public static async Task RunAsync()
    {
        // --- 1. detection ---
        // The exact message from the report (CSOM ServerException).
        var csom429 = new Exception("The remote server returned an error: (429) .");
        Program.Check("throttle: recognizes the CSOM '(429)' ServerException (the reported failure)",
            CsomExtensions.TryGetThrottleWait(csom429, 1, out _), csom429.Message);

        Program.Check("throttle: recognizes '(503)'",
            CsomExtensions.TryGetThrottleWait(new Exception("... error: (503) ..."), 1, out _), "503");
        Program.Check("throttle: recognizes '(504)'",
            CsomExtensions.TryGetThrottleWait(new Exception("... error: (504) ..."), 1, out _), "504");

        // A WebException whose message carries the 429 is detected (Retry-After header parsing lives in the
        // REST client's HttpResponseMessage path; the CSOM path falls back to backoff when there is no
        // inspectable response, which is the shape that reached users).
        Program.Check("throttle: detects a WebException 429",
            CsomExtensions.TryGetThrottleWait(new WebException("The remote server returned an error: (429)."), 1, out _),
            "WebException");

        // Nested (CSOM often wraps the throttle): still detected through InnerException.
        Program.Check("throttle: detects a throttle nested in an inner exception",
            CsomExtensions.TryGetThrottleWait(
                new Exception("wrapper", new WebException("... (429) ...")), 1, out _), "nested");

        // A genuine non-throttle error must NOT be treated as throttling (or a real failure would loop).
        Program.Check("throttle: a 404 is NOT treated as throttling",
            !CsomExtensions.TryGetThrottleWait(new Exception("... error: (404) not found"), 1, out _), "404");
        Program.Check("throttle: 'File Not Found' is NOT throttling",
            !CsomExtensions.TryGetThrottleWait(new Exception("File Not Found."), 1, out _), "not found");

        // Backoff grows with the attempt when there is no Retry-After, and is capped.
        CsomExtensions.TryGetThrottleWait(csom429, 1, out var w1);
        CsomExtensions.TryGetThrottleWait(csom429, 4, out var w4);
        CsomExtensions.TryGetThrottleWait(csom429, 20, out var wBig);
        Program.Check("throttle: backoff increases with attempt", w4 > w1, $"{w1.TotalSeconds}s -> {w4.TotalSeconds}s");
        Program.Check("throttle: backoff is capped at 300s", wBig.TotalSeconds <= 300, $"{wBig.TotalSeconds}s");

        // --- 2. shared per-host gate actually delays traffic after a pause ---
        const string host = "throttle-test.example.com";
        RequestThrottle.Configure(host, 0);   // no rate cap; test only the pause
        RequestThrottle.PauseHost(host, TimeSpan.FromMilliseconds(600));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await RequestThrottle.WaitTurnAsync(host);
        sw.Stop();
        Program.Check("throttle: a paused host actually holds back the next request",
            sw.ElapsedMilliseconds >= 500, $"waited {sw.ElapsedMilliseconds}ms");

        // The longer of two overlapping pauses wins (two concurrent 429s must not shorten the wait).
        RequestThrottle.PauseHost(host, TimeSpan.FromMilliseconds(200));
        RequestThrottle.PauseHost(host, TimeSpan.FromMilliseconds(900));
        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        await RequestThrottle.WaitTurnAsync(host);
        sw2.Stop();
        Program.Check("throttle: the LONGER of two overlapping pauses wins",
            sw2.ElapsedMilliseconds >= 700, $"waited {sw2.ElapsedMilliseconds}ms");

        await Task.CompletedTask;
    }
}

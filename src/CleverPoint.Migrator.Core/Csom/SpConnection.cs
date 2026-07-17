using System.Net;
using CleverPoint.Migrator.Core.Auth;
using CleverPoint.Migrator.Core.Http;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.Core.Csom;

/// <summary>
/// A connection to one SharePoint web: produces authenticated CSOM
/// ClientContexts and shares an SpRestClient for raw REST calls.
/// </summary>
public class SpConnection
{
    public string SiteUrl { get; }
    public string Host { get; }
    public ITokenProvider? Tokens { get; }
    public SpRestClient Rest { get; }
    private readonly (string FedAuth, string RtFa)? _cookies;

    public SpConnection(string siteUrl, ITokenProvider tokens, SpRestClient? rest = null)
    {
        SiteUrl = siteUrl.TrimEnd('/');
        Host = new Uri(siteUrl).Host;
        Tokens = tokens;
        Rest = rest ?? new SpRestClient(tokens);
    }

    /// <summary>Browser-session auth (FedAuth/rtFa cookies from an interactive sign-in).</summary>
    public SpConnection(string siteUrl, string fedAuth, string rtFa, SpRestClient? rest = null)
    {
        SiteUrl = siteUrl.TrimEnd('/');
        Host = new Uri(siteUrl).Host;
        _cookies = (fedAuth, rtFa);
        Rest = rest ?? new SpRestClient(fedAuth, rtFa);
    }

    /// <summary>New CSOM context with auth injection and traffic decoration.</summary>
    public ClientContext CreateContext()
    {
        var ctx = new ClientContext(SiteUrl);
        ctx.ExecutingWebRequest += (_, e) =>
        {
            if (Tokens != null)
            {
                var token = Tokens.GetTokenAsync(Host).GetAwaiter().GetResult();
                e.WebRequestExecutor.RequestHeaders["Authorization"] = "Bearer " + token;
            }
            else if (_cookies is { } c)
            {
                e.WebRequestExecutor.RequestHeaders["Cookie"] = $"FedAuth={c.FedAuth}; rtFa={c.RtFa}";
                // netstandard CSOM has no built-in form-digest handling, so
                // cookie-auth ProcessQuery writes are 403 without this header.
                e.WebRequestExecutor.RequestHeaders["X-RequestDigest"] =
                    Rest.GetFormDigestAsync(SiteUrl).GetAwaiter().GetResult();
            }
            e.WebRequestExecutor.RequestHeaders["User-Agent"] = SpRestClient.UserAgent;
        };
        return ctx;
    }

    /// <summary>A connection to a different web on the same host (re-uses auth and REST client).</summary>
    public SpConnection ForWeb(string siteUrl) =>
        Tokens != null ? new SpConnection(siteUrl, Tokens, Rest)
            : new SpConnection(siteUrl, _cookies!.Value.FedAuth, _cookies.Value.RtFa, Rest);
}

public static class CsomExtensions
{
    /// <summary>
    /// ExecuteQuery that survives throttling. SharePoint answers 429/503 when it wants less traffic, and
    /// CSOM surfaces that as an exception -- so a plain ExecuteQueryAsync turns a "slow down" into a failed
    /// item. This waits and retries instead, and does it politely:
    ///
    ///  - it takes its turn on the SHARED per-host gate, so a 429 raised by ANY concurrent request (REST
    ///    uploads, other parallel workers, another migration in the same process) holds this one back too;
    ///  - it honors the server's Retry-After when present rather than guessing a backoff;
    ///  - it PAUSES the whole host for that delay, so the other workers back off as well.
    ///
    /// Retrying the batch is safe: a throttled request is rejected outright, so nothing was committed.
    /// </summary>
    public static async Task ExecuteWithRetryAsync(this ClientContext ctx, int maxRetries = 6, Action<int, int>? onThrottle = null)
    {
        var host = new Uri(ctx.Url).Host;
        for (var attempt = 1; ; attempt++)
        {
            await Http.RequestThrottle.WaitTurnAsync(host);
            try
            {
                await ctx.ExecuteQueryAsync();
                return;
            }
            catch (Exception ex) when (attempt <= maxRetries && TryGetThrottleWait(ex, attempt, out var wait))
            {
                Http.RequestThrottle.PauseHost(host, wait);
                onThrottle?.Invoke((int)wait.TotalSeconds, attempt);
                Diagnostics.TraceLog.Write("Throttle",
                    $"CSOM throttled on {host} (attempt {attempt}/{maxRetries}); waiting {wait.TotalSeconds:F0}s");
                await Task.Delay(wait);
            }
        }
    }

    /// <summary>
    /// True (with how long to wait) when an exception is SharePoint asking for less traffic. Prefers the
    /// server's Retry-After; falls back to exponential backoff. The message check is the important one in
    /// practice: depending on the stack, CSOM can surface the 429 without an inspectable response object,
    /// which is how "The remote server returned an error: (429)" reached users as a failed file.
    /// </summary>
    /// <summary>
    /// True (with how long to wait) when an exception is SharePoint asking for less traffic. Public so the
    /// throttle handling can be tested against the exact exception shapes SharePoint produces.
    /// </summary>
    public static bool TryGetThrottleWait(Exception ex, int attempt, out TimeSpan wait)
    {
        wait = default;
        var backoff = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt), 300));

        var web = ex as WebException ?? ex.InnerException as WebException;
        if (web?.Response is HttpWebResponse r && IsThrottleStatus((int)r.StatusCode))
        {
            var header = r.Headers["Retry-After"];
            wait = int.TryParse(header, out var secs) && secs > 0 ? TimeSpan.FromSeconds(secs) : backoff;
            return true;
        }

        for (var e = ex; e != null; e = e.InnerException)
            if (e.Message.Contains("(429)") || e.Message.Contains("(503)") || e.Message.Contains("(504)"))
            {
                wait = backoff;
                return true;
            }
        return false;
    }

    private static bool IsThrottleStatus(int status) => status is 429 or 503 or 504;
}

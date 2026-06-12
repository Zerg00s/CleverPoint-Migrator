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
    /// <summary>ExecuteQuery with throttling retry (CSOM throws on 429 with a WebException).</summary>
    public static async Task ExecuteWithRetryAsync(this ClientContext ctx, int maxRetries = 6, Action<int, int>? onThrottle = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await ctx.ExecuteQueryAsync();
                return;
            }
            catch (WebException ex) when (attempt <= maxRetries && IsThrottle(ex))
            {
                var wait = (int)Math.Pow(2, attempt);
                onThrottle?.Invoke(wait, attempt);
                await Task.Delay(TimeSpan.FromSeconds(wait));
            }
        }
    }

    private static bool IsThrottle(WebException ex) =>
        ex.Response is HttpWebResponse r && ((int)r.StatusCode == 429 || (int)r.StatusCode == 503);
}

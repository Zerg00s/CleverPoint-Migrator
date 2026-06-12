using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CleverPoint.Migrator.Core.Http;

/// <summary>
/// HttpClient wrapper for SharePoint REST with Bearer auth, traffic
/// decoration per Microsoft's "Avoid getting throttled" guidance, and
/// automatic 429/503 retry honoring Retry-After. Throttle hits are surfaced
/// through <see cref="OnThrottle"/> so the UI/log can show them.
/// </summary>
public class SpRestClient
{
    /// <summary>Traffic decoration: NONISV|CompanyName|AppName/Version.</summary>
    public const string UserAgent = "NONISV|CleverPoint|Migrator/1.0";

    private readonly HttpClient _http;
    private readonly Auth.ITokenProvider? _tokens;
    private readonly string? _cookieHeader;
    private readonly int _maxRetries;

    /// <summary>Raised when a request was throttled: (url, retryAfterSeconds, attempt).</summary>
    public event Action<string, int, int>? OnThrottle;

    public SpRestClient(Auth.ITokenProvider tokens, HttpClient? http = null, int maxRetries = 6)
    {
        _tokens = tokens;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _maxRetries = maxRetries;
    }

    /// <summary>Browser-session auth: FedAuth/rtFa cookies instead of a Bearer token.</summary>
    public SpRestClient(string fedAuth, string rtFa, HttpClient? http = null, int maxRetries = 6)
    {
        _cookieHeader = $"FedAuth={fedAuth}; rtFa={rtFa}";
        _http = http ?? new HttpClient(new HttpClientHandler { UseCookies = false }) { Timeout = TimeSpan.FromMinutes(10) };
        _maxRetries = maxRetries;
    }

    public async Task<JsonDocument> GetJsonAsync(string url) =>
        JsonDocument.Parse(await SendAsync(HttpMethod.Get, url, null, null));

    public async Task<string> PostAsync(string url, object? body = null, string? digestSiteUrl = null) =>
        await SendAsync(HttpMethod.Post, url, body == null ? null : JsonSerializer.Serialize(body), null);

    /// <summary>POST with a pre-serialized JSON body and explicit content type (odata=verbose endpoints).</summary>
    public async Task<string> PostRawAsync(string url, string json, string contentType = "application/json;odata=verbose") =>
        await SendAsync(HttpMethod.Post, url, json, null, contentType);

    public async Task<byte[]> GetBytesAsync(string url)
    {
        using var request = await BuildRequestAsync(HttpMethod.Get, url, null);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        await EnsureSuccessAsync(response, url);
        return await response.Content.ReadAsByteArrayAsync();
    }

    /// <summary>
    /// True streaming download (ResponseHeadersRead): the returned stream
    /// pulls from the network as it is read. Caller disposes it.
    /// </summary>
    public async Task<Stream> GetStreamAsync(string url)
    {
        var request = await BuildRequestAsync(HttpMethod.Get, url, null);
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            response.Dispose();
            throw new SpRestException((int)response.StatusCode, url, body.Length > 1500 ? body[..1500] : body);
        }
        return await response.Content.ReadAsStreamAsync();
    }

    /// <summary>POST with a binary body (upload-session slices), with throttle retry.</summary>
    public async Task<string> PostBinaryAsync(string url, byte[] data, int count)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = await BuildRequestAsync(HttpMethod.Post, url, null);
            request.Content = new ByteArrayContent(data, 0, count);
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request);
            }
            catch (HttpRequestException) when (attempt <= _maxRetries)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)));
                continue;
            }
            using (response)
            {
                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable && attempt <= _maxRetries)
                {
                    var wait = (int)(response.Headers.RetryAfter?.Delta?.TotalSeconds ?? Math.Pow(2, attempt));
                    OnThrottle?.Invoke(url, wait, attempt);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(wait, 1)));
                    continue;
                }
                await EnsureSuccessAsync(response, url);
                return await response.Content.ReadAsStringAsync();
            }
        }
    }

    public async Task<string> SendAsync(HttpMethod method, string url, string? jsonBody, Dictionary<string, string>? extraHeaders, string? contentType = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = await BuildRequestAsync(method, url, extraHeaders);
            if (jsonBody != null)
            {
                request.Content = new StringContent(jsonBody, Encoding.UTF8);
                request.Content.Headers.ContentType =
                    System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType ?? "application/json");
            }

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request);
            }
            catch (HttpRequestException) when (attempt <= _maxRetries)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)));
                continue;
            }

            using (response)
            {
                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
                {
                    if (attempt > _maxRetries)
                        throw new HttpRequestException($"Still throttled after {_maxRetries} retries: {method} {url}");
                    var wait = (int)(response.Headers.RetryAfter?.Delta?.TotalSeconds ?? Math.Pow(2, attempt));
                    // Pause every concurrent run talking to this host, not just us.
                    RequestThrottle.PauseHost(new Uri(url).Host, TimeSpan.FromSeconds(Math.Max(wait, 1)));
                    OnThrottle?.Invoke(url, wait, attempt);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(wait, 1)));
                    continue;
                }

                await EnsureSuccessAsync(response, url);
                return await response.Content.ReadAsStringAsync();
            }
        }
    }

    private async Task<HttpRequestMessage> BuildRequestAsync(HttpMethod method, string url, Dictionary<string, string>? extraHeaders)
    {
        var host = new Uri(url).Host;
        await RequestThrottle.WaitTurnAsync(host);
        var request = new HttpRequestMessage(method, url);
        if (_tokens != null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _tokens.GetTokenAsync(host));
        else if (_cookieHeader != null)
            request.Headers.TryAddWithoutValidation("Cookie", _cookieHeader);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json") { Parameters = { new NameValueHeaderValue("odata", "nometadata") } });
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        if (extraHeaders != null)
            foreach (var (k, v) in extraHeaders)
                request.Headers.TryAddWithoutValidation(k, v);
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string url)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync();
        throw new SpRestException((int)response.StatusCode, url, body.Length > 1500 ? body[..1500] : body);
    }
}

public class SpRestException : Exception
{
    public int StatusCode { get; }
    public string Url { get; }

    public SpRestException(int statusCode, string url, string body)
        : base($"HTTP {statusCode} from {url}: {body}")
    {
        StatusCode = statusCode;
        Url = url;
    }
}

using System.Text.Json;
using Microsoft.Web.WebView2.WinForms;

namespace CleverPoint.Migrator.SignInHelper;

/// <summary>
/// Tiny Windows-only browser sign-in helper. The cross-platform Photino app
/// can't read the WebView's HttpOnly FedAuth cookie, so it launches this exe,
/// which pops a real WebView2 sign-in window (the same flow the WinForms app
/// uses), captures FedAuth + rtFa via the CookieManager, writes them as JSON
/// to the output file, and exits.
///
/// Usage: CleverPoint.Migrator.SignInHelper.exe &lt;siteUrl&gt; &lt;outFile&gt; [--fresh]
/// Exit codes: 0 = captured, 2 = cancelled / no cookies, 3 = bad args.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 2) return 3;
        var siteUrl = args[0].TrimEnd('/');
        var outFile = args[1];
        var forceFresh = args.Contains("--fresh");

        ApplicationConfiguration.Initialize();
        var form = new SignInForm(siteUrl, forceFresh, outFile);
        Application.Run(form);
        return form.Captured ? 0 : 2;
    }
}

internal sealed class SignInForm : Form
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly string _siteUrl;
    private readonly bool _forceFresh;
    private readonly string _outFile;
    private bool _busy;
    private bool _retried;
    public bool Captured { get; private set; }

    public SignInForm(string siteUrl, bool forceFresh, string outFile)
    {
        _siteUrl = siteUrl;
        _forceFresh = forceFresh;
        _outFile = outFile;
        Text = $"Sign in - {new Uri(siteUrl).Host}";
        Width = 560;
        Height = 720;
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(_web);
        Load += async (_, _) => await StartAsync();
    }

    private async Task StartAsync()
    {
        await _web.EnsureCoreWebView2Async();
        if (_forceFresh)
            _web.CoreWebView2.CookieManager.DeleteAllCookies();
        _web.CoreWebView2.NavigationCompleted += async (_, _) => await ProbeAsync();
        _web.CoreWebView2.Navigate(_siteUrl);
    }

    private async Task ProbeAsync()
    {
        if (_busy || Captured) return;
        var cookies = await _web.CoreWebView2.CookieManager.GetCookiesAsync(_siteUrl);
        var fedAuth = cookies.FirstOrDefault(c => c.Name.Equals("FedAuth", StringComparison.OrdinalIgnoreCase));
        var rtFa = cookies.FirstOrDefault(c => c.Name.Equals("rtFa", StringComparison.OrdinalIgnoreCase));
        if (fedAuth is null) return;

        // Validate before trusting: a profile can hold cookies SharePoint rejects.
        _busy = true;
        try
        {
            using var http = new HttpClient(new HttpClientHandler { UseCookies = false });
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_siteUrl}/_api/web?$select=Title");
            req.Headers.TryAddWithoutValidation("Cookie", $"FedAuth={fedAuth.Value}; rtFa={rtFa?.Value ?? ""}");
            req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            var resp = await http.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                File.WriteAllText(_outFile, JsonSerializer.Serialize(new { fedAuth = fedAuth.Value, rtFa = rtFa?.Value ?? "" }));
                Captured = true;
                Close();
                return;
            }
            if ((int)resp.StatusCode is 401 or 403 && !_retried)
            {
                _retried = true;
                _web.CoreWebView2.CookieManager.DeleteAllCookies();
                _web.CoreWebView2.Navigate(_siteUrl);
            }
        }
        catch { /* keep the window open for a manual retry */ }
        finally { _busy = false; }
    }
}

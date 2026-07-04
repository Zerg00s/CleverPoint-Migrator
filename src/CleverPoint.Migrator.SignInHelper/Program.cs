using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
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
/// Exit codes: 0 = captured, 2 = cancelled / no cookies, 3 = bad args, 4 = init failed.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Catch managed exceptions so a WebView2 failure shows a readable message
        // and a log line instead of tearing the process down. (A pure native
        // access violation inside WebView2 can still hard-crash, but the log
        // breadcrumbs below tell us how far it got.)
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Fatal("ThreadException", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Fatal("UnhandledException", e.ExceptionObject);

        if (args.Length < 2) return 3;
        var siteUrl = args[0].TrimEnd('/');
        var outFile = args[1];
        var forceFresh = args.Contains("--fresh");

        Log.Info($"start host={SafeHost(siteUrl)} fresh={forceFresh}");
        ApplicationConfiguration.Initialize();
        var form = new SignInForm(siteUrl, forceFresh, outFile);
        Application.Run(form);
        Log.Info($"exit captured={form.Captured}");
        return form.Captured ? 0 : 2;
    }

    private static string SafeHost(string url)
    {
        try { return new Uri(url).Host; } catch { return "?"; }
    }
}

/// <summary>
/// Minimal file logger shared with the main app's log folder so both write to
/// %AppData%\CleverPoint Migrator\logs (the "Open Logs" button opens it).
/// Never throws.
/// </summary>
internal static class Log
{
    private static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CleverPoint Migrator", "logs");
    private static string FilePath => Path.Combine(Folder, "signin-helper.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Fatal(string context, object? detail)
    {
        Write("FATAL", context + (detail != null ? Environment.NewLine + detail : ""));
        try
        {
            MessageBox.Show(
                "The sign-in window could not start.\r\n\r\n" + detail,
                "Sign-in helper error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { /* headless / no message pump */ }
    }

    private static void Write(string level, string message)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.AppendAllText(FilePath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {level} {message}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch { /* logging must never throw */ }
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
        try
        {
            // Persistent profile so a previous sign-in is remembered: re-auth becomes
            // one click (or silent) instead of a full username/password every time.
            var udf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CleverPoint Migrator", "WebView2");
            Directory.CreateDirectory(udf);
            Log.Info("creating WebView2 environment");
            var env = await CoreWebView2Environment.CreateAsync(null, udf);
            Log.Info("ensuring CoreWebView2");
            await _web.EnsureCoreWebView2Async(env);
            if (_forceFresh)
                _web.CoreWebView2.CookieManager.DeleteAllCookies();
            _web.CoreWebView2.NavigationCompleted += async (_, _) => await ProbeAsync();
            Log.Info("navigating");
            _web.CoreWebView2.Navigate(_siteUrl);
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            Log.Fatal("WebView2 runtime missing", ex.Message
                + "\r\n\r\nInstall the Microsoft Edge WebView2 Runtime from "
                + "https://developer.microsoft.com/microsoft-edge/webview2/ then try again.");
            Close();
        }
        catch (Exception ex)
        {
            Log.Fatal("WebView2 init failed", ex);
            Close();
        }
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

using CleverPoint.Migrator.App.Theme;
using Microsoft.Web.WebView2.WinForms;

namespace CleverPoint.Migrator.App.Screens;

/// <summary>
/// Interactive SharePoint sign-in (same approach as the proven sample app):
/// a WebView2 window navigates to the site; once SharePoint issues the
/// FedAuth session cookie the form closes and hands the cookies back.
/// Cookies are kept in memory per session (re-auth pops this form again
/// whenever a session expires mid-run).
/// </summary>
public class BrowserLoginForm : Form
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly string _siteUrl;
    private readonly bool _forceFresh;
    private bool _retriedFresh;
    private bool _validating;

    public string? FedAuth { get; private set; }
    public string? RtFa { get; private set; }

    public BrowserLoginForm(string siteUrl, bool forceFresh = false)
    {
        _forceFresh = forceFresh;
        _siteUrl = siteUrl.TrimEnd('/');
        Text = $"Sign in - {new Uri(_siteUrl).Host}";
        Size = new Size(560, 720);
        StartPosition = FormStartPosition.CenterParent;
        Icon = AppIcon.Create();
        BackColor = Brand.Surface;
        Controls.Add(_web);
        Load += async (_, _) => await StartAsync();
    }

    private async Task StartAsync()
    {
        await _web.EnsureCoreWebView2Async();
        // A stale session in the WebView2 profile would silently SSO and hand
        // back the same dead cookies forever - start clean when asked to.
        if (_forceFresh)
            _web.CoreWebView2.CookieManager.DeleteAllCookies();
        _web.CoreWebView2.NavigationCompleted += async (_, _) => await ProbeCookiesAsync();
        _web.CoreWebView2.Navigate(_siteUrl);
    }

    private async Task ProbeCookiesAsync()
    {
        if (_validating) return;
        var cookies = await _web.CoreWebView2.CookieManager.GetCookiesAsync(_siteUrl);
        var fedAuth = cookies.FirstOrDefault(c => c.Name.Equals("FedAuth", StringComparison.OrdinalIgnoreCase));
        var rtFa = cookies.FirstOrDefault(c => c.Name.Equals("rtFa", StringComparison.OrdinalIgnoreCase));
        if (fedAuth == null) return;

        // Never trust captured cookies blindly: validate with a real REST
        // call. A profile can hold cookies SharePoint already rejects.
        _validating = true;
        try
        {
            var rest = new Core.Http.SpRestClient(fedAuth.Value, rtFa?.Value ?? "", maxRetries: 1);
            using var _ = await rest.GetJsonAsync($"{_siteUrl}/_api/web?$select=Title");
            FedAuth = fedAuth.Value;
            RtFa = rtFa?.Value ?? "";
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Core.Http.SpRestException ex) when (ex.StatusCode is 401 or 403)
        {
            if (_retriedFresh) return;  // window stays open for a manual retry
            _retriedFresh = true;
            Text = $"Sign in again - the previous session expired ({new Uri(_siteUrl).Host})";
            _web.CoreWebView2.CookieManager.DeleteAllCookies();
            _web.CoreWebView2.Navigate(_siteUrl);
        }
        finally
        {
            _validating = false;
        }
    }

    /// <summary>Shows the sign-in window and returns a connected SpConnection, or null when dismissed.</summary>
    public static Core.Csom.SpConnection? Connect(IWin32Window owner, string siteUrl, bool forceFresh = false)
    {
        using var form = new BrowserLoginForm(siteUrl, forceFresh);
        return form.ShowDialog(owner) == DialogResult.OK && form.FedAuth != null
            ? new Core.Csom.SpConnection(siteUrl, form.FedAuth, form.RtFa ?? "")
            : null;
    }
}

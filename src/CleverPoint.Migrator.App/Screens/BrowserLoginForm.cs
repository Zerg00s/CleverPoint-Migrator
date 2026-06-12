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

    public string? FedAuth { get; private set; }
    public string? RtFa { get; private set; }

    public BrowserLoginForm(string siteUrl)
    {
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
        _web.CoreWebView2.NavigationCompleted += async (_, _) => await ProbeCookiesAsync();
        _web.CoreWebView2.Navigate(_siteUrl);
    }

    private async Task ProbeCookiesAsync()
    {
        var cookies = await _web.CoreWebView2.CookieManager.GetCookiesAsync(_siteUrl);
        var fedAuth = cookies.FirstOrDefault(c => c.Name.Equals("FedAuth", StringComparison.OrdinalIgnoreCase));
        var rtFa = cookies.FirstOrDefault(c => c.Name.Equals("rtFa", StringComparison.OrdinalIgnoreCase));
        if (fedAuth != null)
        {
            FedAuth = fedAuth.Value;
            RtFa = rtFa?.Value ?? "";
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    /// <summary>Shows the sign-in window and returns a connected SpConnection, or null when dismissed.</summary>
    public static Core.Csom.SpConnection? Connect(IWin32Window owner, string siteUrl)
    {
        using var form = new BrowserLoginForm(siteUrl);
        return form.ShowDialog(owner) == DialogResult.OK && form.FedAuth != null
            ? new Core.Csom.SpConnection(siteUrl, form.FedAuth, form.RtFa ?? "")
            : null;
    }
}

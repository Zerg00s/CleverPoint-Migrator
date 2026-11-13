using System.Collections.Concurrent;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace CleverPoint.Migrator.Core.Auth;

/// <summary>The saved sign-in can no longer be renewed silently; the user has to sign in again.</summary>
public sealed class SignInRequiredException : Exception
{
    public SignInRequiredException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Who is signed in on a delegated connection.</summary>
public sealed record SignedInAccount(string Username, string HomeAccountId, string TenantId);

/// <summary>
/// Delegated (signed-in user) access tokens for SharePoint Online through the tenant's own
/// "CleverPoint Migrator" app registration, a public client with no secret and no certificate.
///
/// The app can never do more than the signed-in user can: the token carries the smaller of the app's
/// delegated scopes and the user's own rights. The user signs in once in the system browser; after
/// that the refresh token in the encrypted cache renews access silently, including in the middle of
/// a long run and in a later process (the command line shares the same cache file).
/// </summary>
public sealed class DelegatedTokenProvider : ITokenProvider
{
    /// <summary>Loopback redirect registered on the app. Entra ignores the port for http://localhost.</summary>
    public const string RedirectUri = "http://localhost";

    private const string SignedInPage =
        "<html><body style='font-family:Segoe UI,sans-serif;margin:40px'><h2>Sign-in complete.</h2>" +
        "<p>You can close this tab and return to CleverPoint Migrator.</p></body></html>";
    private const string FailedPage =
        "<html><body style='font-family:Segoe UI,sans-serif;margin:40px'><h2>Sign-in did not complete.</h2>" +
        "<p>Return to CleverPoint Migrator for details.</p></body></html>";

    private readonly IPublicClientApplication _app;
    private readonly string _cachePath;
    private readonly SemaphoreSlim _init = new(1, 1);
    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset Expires)> _tokens = new(StringComparer.OrdinalIgnoreCase);
    private bool _cacheRegistered;
    private string? _homeAccountId;

    public string TenantId { get; }
    public string ClientId { get; }

    /// <param name="cachePath">Token cache file, shared by every process of this user. Encrypted with DPAPI on Windows.</param>
    /// <param name="homeAccountId">The account to use when several are cached; null takes the only or first one.</param>
    public DelegatedTokenProvider(string tenantId, string clientId, string cachePath, string? homeAccountId = null)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) throw new ArgumentException("A tenant ID is required.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(clientId)) throw new ArgumentException("An app (client) ID is required.", nameof(clientId));
        TenantId = tenantId.Trim();
        ClientId = clientId.Trim();
        _cachePath = cachePath;
        _homeAccountId = string.IsNullOrWhiteSpace(homeAccountId) ? null : homeAccountId;
        _app = PublicClientApplicationBuilder.Create(ClientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, TenantId)
            .WithRedirectUri(RedirectUri)
            .Build();
    }

    /// <summary>Everything the tenant consented to for this SharePoint host.</summary>
    private static string[] ScopesFor(string sharePointHost) => new[] { $"https://{sharePointHost}/.default" };

    public async Task<string> GetTokenAsync(string sharePointHost)
    {
        if (_tokens.TryGetValue(sharePointHost, out var cached) && cached.Expires > DateTimeOffset.UtcNow.AddMinutes(5))
            return cached.Token;

        var account = await FindAccountAsync().ConfigureAwait(false)
            ?? throw new SignInRequiredException($"Not signed in to {sharePointHost}. Sign in to this connection first.");
        try
        {
            var result = await _app.AcquireTokenSilent(ScopesFor(sharePointHost), account).ExecuteAsync().ConfigureAwait(false);
            _tokens[sharePointHost] = (result.AccessToken, result.ExpiresOn);
            return result.AccessToken;
        }
        catch (MsalUiRequiredException ex)
        {
            throw new SignInRequiredException(
                $"The sign-in for {sharePointHost} has expired or needs your attention ({ex.ErrorCode}). Sign in to this connection again.", ex);
        }
    }

    /// <summary>The cached account, or null when nobody is signed in. Does not prove the token can still be renewed.</summary>
    public async Task<SignedInAccount?> GetAccountAsync()
    {
        var account = await FindAccountAsync().ConfigureAwait(false);
        return account is null ? null : new SignedInAccount(account.Username, account.HomeAccountId.Identifier, TenantId);
    }

    /// <summary>
    /// Opens the system browser for the user to sign in, and stores the result in the cache.
    /// Gives up after five minutes so a closed browser tab cannot hang the caller forever.
    /// </summary>
    public async Task<SignedInAccount> SignInAsync(string sharePointHost, CancellationToken ct = default)
    {
        await EnsureCacheAsync().ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            var result = await _app.AcquireTokenInteractive(ScopesFor(sharePointHost))
                .WithUseEmbeddedWebView(false)
                .WithPrompt(Prompt.SelectAccount)
                .WithSystemWebViewOptions(new SystemWebViewOptions { HtmlMessageSuccess = SignedInPage, HtmlMessageError = FailedPage })
                .ExecuteAsync(timeout.Token).ConfigureAwait(false);
            _homeAccountId = result.Account.HomeAccountId.Identifier;
            _tokens[sharePointHost] = (result.AccessToken, result.ExpiresOn);
            return new SignedInAccount(result.Account.Username, _homeAccountId, result.TenantId ?? TenantId);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Sign-in timed out after 5 minutes.");
        }
        catch (MsalException ex)
        {
            throw new InvalidOperationException(Explain(ex), ex);
        }
    }

    /// <summary>Forgets the signed-in account on this machine. Does not revoke anything in the tenant.</summary>
    public async Task SignOutAsync()
    {
        _tokens.Clear();
        var account = await FindAccountAsync().ConfigureAwait(false);
        if (account != null) await _app.RemoveAsync(account).ConfigureAwait(false);
        _homeAccountId = null;
    }

    private async Task<IAccount?> FindAccountAsync()
    {
        await EnsureCacheAsync().ConfigureAwait(false);
        if (_homeAccountId != null)
        {
            var exact = await _app.GetAccountAsync(_homeAccountId).ConfigureAwait(false);
            if (exact != null) return exact;
        }
        var all = await _app.GetAccountsAsync().ConfigureAwait(false);
        return all.FirstOrDefault();
    }

    private async Task EnsureCacheAsync()
    {
        if (_cacheRegistered) return;
        await _init.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_cacheRegistered) return;
            var folder = Path.GetDirectoryName(Path.GetFullPath(_cachePath))!;
            Directory.CreateDirectory(folder);
            var storage = new StorageCreationPropertiesBuilder(Path.GetFileName(_cachePath), folder)
                // Windows protects the file with DPAPI for the current user without further setup.
                .WithMacKeyChain("CleverPoint Migrator", "token-cache")
                // Linux and WSL are development hosts only; there is no keyring to rely on there.
                .WithLinuxUnprotectedFile()
                .Build();
            var helper = await MsalCacheHelper.CreateAsync(storage).ConfigureAwait(false);
            helper.RegisterCache(_app.UserTokenCache);
            _cacheRegistered = true;
        }
        finally
        {
            _init.Release();
        }
    }

    /// <summary>Turns the common Entra failures into something the user can act on.</summary>
    internal static string Explain(MsalException ex)
    {
        var message = ex.Message ?? "";
        if (message.Contains("AADSTS700016") || message.Contains("AADSTS7000218"))
            return "The CleverPoint Migrator app was not found in this tenant, or it is not set up as a public client. " +
                   "Run \"Set up for this tenant\" again, or check the app (client) ID.";
        if (message.Contains("AADSTS65001") || message.Contains("AADSTS90094") || message.Contains("AADSTS65004") || ex.ErrorCode == "access_denied")
            return "The app has not been approved for this tenant yet. An administrator needs to grant consent " +
                   "(use \"Set up for this tenant\", or send them the admin consent link).";
        if (message.Contains("AADSTS50020") || message.Contains("AADSTS90072"))
            return "That account does not belong to this tenant. Sign in with an account from the tenant that owns the site.";
        if (ex.ErrorCode == "authentication_canceled")
            return "Sign-in was cancelled.";
        var firstLine = message.Split('\n')[0].Trim();
        return $"Sign-in failed: {firstLine}";
    }
}

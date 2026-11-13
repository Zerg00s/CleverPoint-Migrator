using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace CleverPoint.Migrator.Core.Auth;

/// <summary>
/// Finds the Entra tenant that owns a SharePoint host, without signing in.
///
/// SharePoint answers an unauthenticated bearer request with
/// <c>WWW-Authenticate: Bearer realm="{tenant id}", ...</c>. That lets the sign-in go straight to the
/// right directory, which matters for consultants and guests whose account lives in several tenants.
/// </summary>
public static class TenantDiscovery
{
    private static readonly Regex Realm = new("realm=\"(?<id>[0-9a-fA-F-]{36})\"", RegexOptions.Compiled);

    /// <summary>The tenant ID (a GUID) for e.g. <c>contoso.sharepoint.com</c>. Throws when the host is not SharePoint Online.</summary>
    public static async Task<string> GetTenantIdAsync(string sharePointHost, HttpClient? http = null, CancellationToken ct = default)
    {
        var owned = http is null;
        http ??= new HttpClient();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{sharePointHost}/_vti_bin/client.svc");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer");
            request.Headers.UserAgent.ParseAdd(Http.SpRestClient.UserAgent);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            foreach (var header in response.Headers.WwwAuthenticate)
            {
                var match = Realm.Match(header.ToString());
                if (match.Success) return match.Groups["id"].Value.ToLowerInvariant();
            }
            throw new InvalidOperationException(
                $"Could not find the tenant for {sharePointHost} (HTTP {(int)response.StatusCode}). Check that this is a SharePoint Online address.");
        }
        finally
        {
            if (owned) http.Dispose();
        }
    }
}

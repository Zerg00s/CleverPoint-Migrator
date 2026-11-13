using System.Text.Json;

namespace CleverPoint.Migrator.Core.Hosting;

/// <summary>
/// The small file that carries a "Microsoft sign-in" connection to another computer: site, tenant ID
/// and app (client) ID. It holds no secret. The tenant's sign-in app is a public client, and each
/// person still signs in with their own account on their own machine.
/// </summary>
public static class ConnectionFile
{
    private const string Kind = "cleverpoint-migrator-connection";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private sealed class Payload
    {
        public string? kind { get; set; }
        public int version { get; set; }
        public string? siteUrl { get; set; }
        public string? tenantId { get; set; }
        public string? appId { get; set; }
    }

    public static string Serialize(SavedConnection c) => JsonSerializer.Serialize(new Payload
    {
        kind = Kind,
        version = 1,
        siteUrl = c.SiteUrl.TrimEnd('/'),
        tenantId = c.TenantId,
        appId = c.AppId,
    }, Json);

    /// <summary>Reads a connection file. Only well-formed values are accepted; nothing in it is trusted beyond that.</summary>
    public static bool TryParse(string json, out string siteUrl, out string tenantId, out string appId, out string error)
    {
        siteUrl = tenantId = appId = error = "";
        Payload? p;
        try { p = JsonSerializer.Deserialize<Payload>(json); }
        catch (JsonException) { p = null; }
        if (p is null || p.kind != Kind)
        {
            error = "That is not a CleverPoint Migrator connection file.";
            return false;
        }
        if (!Uri.TryCreate(p.siteUrl, UriKind.Absolute, out var site) || site.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(site.Host))
        {
            error = "The connection file has no valid site address.";
            return false;
        }
        if (!Guid.TryParse(p.tenantId, out var tenant) || !Guid.TryParse(p.appId, out var app))
        {
            error = "The connection file has no valid tenant ID or app (client) ID.";
            return false;
        }
        siteUrl = site.AbsoluteUri.TrimEnd('/');
        tenantId = tenant.ToString();
        appId = app.ToString();
        return true;
    }
}

using CleverPoint.Migrator.Core.Csom;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.Core.Operations;

/// <summary>
/// Resolves source-site user ids to target-site user ids without any Entra
/// access: source user ids are translated to login/email via the source
/// web's site users, then ensured on the target web. A manual mapping table
/// (login or email -> target login) takes precedence; unresolved users fall
/// back to a configurable account.
/// </summary>
public class UserResolver
{
    private readonly ClientContext _sourceCtx;
    private readonly ClientContext _targetCtx;
    private readonly Dictionary<int, (string Login, string Email, string Title)> _sourceUsersById = new();
    private readonly Dictionary<string, int> _targetIdByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _manualMap;
    private readonly string? _fallbackLogin;

    /// <summary>Source users that could not be resolved on the target (login -> reason).</summary>
    public Dictionary<string, string> Unresolved { get; } = new(StringComparer.OrdinalIgnoreCase);

    public UserResolver(ClientContext sourceCtx, ClientContext targetCtx,
        Dictionary<string, string>? manualMap = null, string? fallbackLogin = null)
    {
        _sourceCtx = sourceCtx;
        _targetCtx = targetCtx;
        _manualMap = manualMap ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _fallbackLogin = fallbackLogin;
    }

    /// <summary>
    /// A resolver for a parallel-transfer worker: its own source/target contexts, but SEEDED
    /// with this (already primed + pre-resolved) resolver's maps so the hot path is all cache
    /// hits and no worker performs concurrent EnsureUser on a shared context. Call only AFTER
    /// PrimeSourceUsersAsync + PreResolveAsync on the parent, on a single thread.
    /// </summary>
    public UserResolver SnapshotFor(ClientContext workerSource, ClientContext workerTarget)
    {
        var clone = new UserResolver(workerSource, workerTarget, _manualMap, _fallbackLogin);
        foreach (var kv in _sourceUsersById) clone._sourceUsersById[kv.Key] = kv.Value;
        foreach (var kv in _targetIdByKey) clone._targetIdByKey[kv.Key] = kv.Value;
        return clone;
    }

    /// <summary>Preloads all source site users in one round trip. Call once before copying.</summary>
    public async Task PrimeSourceUsersAsync()
    {
        var users = _sourceCtx.Web.SiteUsers;
        _sourceCtx.Load(users, u => u.Include(x => x.Id, x => x.LoginName, x => x.Email, x => x.Title));
        await _sourceCtx.ExecuteWithRetryAsync();
        foreach (var u in users)
            _sourceUsersById[u.Id] = (u.LoginName, u.Email ?? "", u.Title ?? "");
    }

    public (string Login, string Email, string Title)? GetSourceUser(int sourceUserId) =>
        _sourceUsersById.TryGetValue(sourceUserId, out var u) ? u : null;

    /// <summary>
    /// Resolves a set of source user ids upfront. MUST be called before item
    /// writes begin: resolution executes queries on the target context, which
    /// would otherwise flush half-built items and silently drop their field
    /// values (verified live on 2026-06-11).
    /// </summary>
    public async Task PreResolveAsync(IEnumerable<int> sourceUserIds)
    {
        foreach (var id in sourceUserIds.Distinct())
            await ResolveAsync(id);
    }

    /// <summary>
    /// Maps a source user id to a target user id. Returns null when the user
    /// cannot be resolved and no fallback is configured.
    /// </summary>
    public async Task<int?> ResolveAsync(int sourceUserId)
    {
        if (!_sourceUsersById.TryGetValue(sourceUserId, out var src))
        {
            // Not in the prime cache (deleted user etc.): try a direct lookup.
            try
            {
                var u = _sourceCtx.Web.GetUserById(sourceUserId);
                _sourceCtx.Load(u, x => x.LoginName, x => x.Email, x => x.Title);
                await _sourceCtx.ExecuteWithRetryAsync();
                src = (u.LoginName, u.Email ?? "", u.Title ?? "");
                _sourceUsersById[sourceUserId] = src;
            }
            catch
            {
                Unresolved[$"#{sourceUserId}"] = "source user id not found";
                return await ResolveFallbackAsync();
            }
        }

        return await ResolveByLoginAsync(src.Login, src.Email);
    }

    /// <summary>
    /// Resolves a source user id to the TARGET login name (after mapping and
    /// fallback). Needed for ValidateUpdateListItem claims keys on folders.
    /// </summary>
    public async Task<string?> ResolveTargetLoginAsync(int sourceUserId)
    {
        var id = await ResolveAsync(sourceUserId);
        if (!id.HasValue) return null;
        if (_targetLoginById.TryGetValue(id.Value, out var login)) return login;

        var user = _targetCtx.Web.GetUserById(id.Value);
        _targetCtx.Load(user, x => x.LoginName);
        await _targetCtx.ExecuteWithRetryAsync();
        _targetLoginById[id.Value] = user.LoginName;
        return user.LoginName;
    }

    private readonly Dictionary<int, string> _targetLoginById = new();

    /// <summary>Resolves a login/email directly (used for permissions and manual checks).</summary>
    public async Task<int?> ResolveByLoginAsync(string login, string email = "")
    {
        // Manual mapping first: keyed by full login, bare login, or email.
        var candidates = BuildCandidates(login, email);
        foreach (var key in candidates)
        {
            if (_manualMap.TryGetValue(key, out var mapped))
            {
                candidates = new List<string> { mapped };
                break;
            }
        }

        foreach (var candidate in candidates)
        {
            if (_targetIdByKey.TryGetValue(candidate, out var cachedId))
                return cachedId < 0 ? null : cachedId;

            try
            {
                var ensured = _targetCtx.Web.EnsureUser(candidate);
                _targetCtx.Load(ensured, x => x.Id);
                // Retry throttles here, so a transient 429/503 does not get mistaken for
                // "user does not exist" and negative-cached for the rest of the run (which
                // would rewrite authorship to the fallback account at scale).
                await _targetCtx.ExecuteWithRetryAsync();
                _targetIdByKey[candidate] = ensured.Id;
                return ensured.Id;
            }
            catch
            {
                _targetIdByKey[candidate] = -1;  // genuine miss (after throttle retries): negative-cache
            }
        }

        Unresolved[login] = "no match on target";
        return await ResolveFallbackAsync();
    }

    private async Task<int?> ResolveFallbackAsync()
    {
        if (string.IsNullOrEmpty(_fallbackLogin)) return null;
        return await ResolveByLoginNoFallbackAsync(_fallbackLogin);
    }

    private async Task<int?> ResolveByLoginNoFallbackAsync(string login)
    {
        if (_targetIdByKey.TryGetValue(login, out var cached))
            return cached < 0 ? null : cached;
        try
        {
            var ensured = _targetCtx.Web.EnsureUser(login);
            _targetCtx.Load(ensured, x => x.Id);
            await _targetCtx.ExecuteWithRetryAsync();
            _targetIdByKey[login] = ensured.Id;
            return ensured.Id;
        }
        catch
        {
            _targetIdByKey[login] = -1;
            return null;
        }
    }

    private static List<string> BuildCandidates(string login, string email)
    {
        var list = new List<string>();
        if (!string.IsNullOrEmpty(login)) list.Add(login);

        // Claims login "i:0#.f|membership|user@x.com" -> bare UPN as second candidate
        var pipe = login.LastIndexOf('|');
        if (pipe > 0 && pipe < login.Length - 1)
        {
            var bare = login[(pipe + 1)..];
            if (!list.Contains(bare, StringComparer.OrdinalIgnoreCase)) list.Add(bare);
        }
        if (!string.IsNullOrEmpty(email) && !list.Contains(email, StringComparer.OrdinalIgnoreCase))
            list.Add(email);
        return list;
    }
}

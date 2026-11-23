using System.Text.Json;
using System.Text.RegularExpressions;
using Fyrd.Core.CloudSessions;

namespace Fyrd.Core.Hub;

/// <summary>
/// One session → pull request relationship: <c>Source</c> is <see cref="SessionPullRequests.FromBlock"/> (the session
/// named it in a <c>fyrd-prs</c> block), <see cref="SessionPullRequests.FromBranch"/> (its head is one of the session's
/// branches) or <see cref="SessionPullRequests.FromStack"/> (an open PR stacked with one of those).
/// </summary>
public sealed record SessionPullRequest(string Url, DateTimeOffset FirstSeen, string Source);

/// <summary>
/// The persisted cache of which pull requests each session works on, in <c>hub/session-prs.json</c>. It only grows
/// while a session lives: a relationship is never dropped because its PR closed or merged or its branch is gone. A
/// session's entries go only when <see cref="ReapAsync"/> finds the session archived or deleted (404), checked by id at
/// most once per <see cref="ReapInterval"/>; any other error keeps them. Thread safe.
/// </summary>
public sealed partial class SessionPullRequests(FyrdPaths paths, TimeProvider time)
{
    public const string FromBlock = "block", FromBranch = "branch", FromStack = "stack";

    /// <summary>
    /// The most block-sourced PRs one session keeps: block URLs are model output, and each PR a session has is read from
    /// GitHub when its details open. Relationships found on GitHub (branch and stack) are not capped.
    /// </summary>
    public const int MaxBlockPerSession = 50;

    public static readonly TimeSpan ReapInterval = TimeSpan.FromHours(1);

    // The cloud client's own rule: an id it would refuse could never be asked about, so it would never be reaped.
    [GeneratedRegex("^session_[A-Za-z0-9]+$")]
    private static partial Regex SessionIdPattern();

    /// <summary>
    /// A URL the store keeps: exactly what <see cref="PullRequestBlock.Normalize"/> builds (lower case, strict owner, repo
    /// and number, no <c>..</c>, no leading zeros, a number that fits an <see cref="int"/>), so <see cref="PullRequestUrl.Parse"/>
    /// never throws on a cached URL.
    /// </summary>
    public static bool IsStorable(string? url) => url is not null && PullRequestBlock.Normalize(url) == url;

    /// <summary>A session id the store keeps: one the cloud API accepts.</summary>
    public static bool IsSessionId(string? id) => id is not null && SessionIdPattern().IsMatch(id);

    private sealed record Store(DateTimeOffset? ReapedAt, Dictionary<string, List<SessionPullRequest>> Sessions);

    private readonly object _gate = new();
    private readonly string _file = Path.Combine(paths.HubDir, "session-prs.json");
    private Store? _store;

    /// <summary>The session's cached PRs, oldest first; empty when it has none.</summary>
    public IReadOnlyList<SessionPullRequest> For(string sessionId)
    {
        lock (_gate)
            return Load().Sessions.TryGetValue(sessionId, out var list) ? list.ToList() : [];
    }

    /// <summary>Every session id that has cached PRs.</summary>
    public IReadOnlyList<string> SessionIds()
    {
        lock (_gate) return Load().Sessions.Keys.Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Adds each URL, lower-cased (<see cref="PullRequestUrl.Canonical"/>), that is valid (<see cref="IsStorable"/>) and that
    /// the session does not have yet, with
    /// this source, and persists once. An invalid URL is skipped, and nothing is added for an id that is not
    /// <see cref="IsSessionId"/>; past <see cref="MaxBlockPerSession"/> block URLs more are skipped. A URL already
    /// cached keeps its first-seen time and source. Returns the URLs added.
    /// </summary>
    public IReadOnlyList<string> Add(string sessionId, IEnumerable<string> urls, string source, DateTimeOffset? at = null)
    {
        if (!IsSessionId(sessionId)) return [];
        if (source is not (FromBlock or FromBranch or FromStack)) throw new ArgumentException($"Unknown source '{source}'.", nameof(source));
        var when = at ?? time.GetUtcNow();
        lock (_gate)
        {
            var store = Load();
            store.Sessions.TryGetValue(sessionId, out var list);
            list ??= [];
            var added = new List<string>();
            var blockCount = list.Count(p => p.Source == FromBlock);
            foreach (var raw in urls)
            {
                var url = raw is null ? null : PullRequestUrl.Canonical(raw);
                if (!IsStorable(url) || list.Any(p => p.Url == url)) continue;
                if (source == FromBlock && blockCount >= MaxBlockPerSession) break;
                list.Add(new SessionPullRequest(url, when, source));
                added.Add(url);
                if (source == FromBlock) blockCount++;
            }
            if (added.Count == 0) return [];
            store.Sessions[sessionId] = list;
            Save(store);
            return added;
        }
    }

    /// <summary>When the last reap finished, or null before the first.</summary>
    public DateTimeOffset? ReapedAt
    {
        get { lock (_gate) return Load().ReapedAt; }
    }

    /// <summary>
    /// At most once per <see cref="ReapInterval"/>: asks the cloud about every cached session that is not in
    /// <paramref name="live"/> (sessions the hub listing just read as not archived) and drops the entries of each one
    /// that is archived or gone (404). A transient error (any other failure) keeps a session's entries for the next
    /// reap; an auth failure stops the reap and propagates, so the poll reports the sign-out. Returns the ids dropped.
    /// </summary>
    public async Task<IReadOnlyList<string>> ReapAsync(ICloudSessions cloud, IReadOnlySet<string> live, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        List<string> candidates;
        lock (_gate)
        {
            var store = Load();
            if (store.Sessions.Count == 0) return [];
            if (store.ReapedAt is { } last && now - last < ReapInterval && now >= last) return [];
            candidates = store.Sessions.Keys.Where(id => !live.Contains(id)).Order(StringComparer.Ordinal).ToList();
        }
        var dead = new List<string>();
        foreach (var id in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var s = await cloud.GetAsync(id, ct);
                if (s.SessionStatus == "archived") dead.Add(id);
            }
            catch (CloudSessionApiException e) when (e.Status == 404) { dead.Add(id); }
            catch (Exception e) when (e is not (CloudSessionAuthException or ClaudeAuthMissingException)
                                      && (e is not OperationCanceledException || !ct.IsCancellationRequested))
            {
                // Unknown liveness: keep it.
            }
        }
        lock (_gate)
        {
            var store = Load();
            foreach (var id in dead) store.Sessions.Remove(id);
            Save(store with { ReapedAt = now });
        }
        return dead;
    }

    private Store Load()
    {
        if (_store is not null) return _store;
        Store? read = null;
        if (File.Exists(_file))
        {
            try { read = JsonSerializer.Deserialize<Store>(File.ReadAllText(_file), Json.Options); }
            catch (JsonException)
            {
                // Keep the unreadable file for inspection instead of letting the next write silently replace it.
                File.Move(_file, _file + ".corrupt", overwrite: true);
            }
        }
        var sessions = new Dictionary<string, List<SessionPullRequest>>(StringComparer.Ordinal);
        foreach (var (id, list) in read?.Sessions ?? [])
        {
            // A hand-edited entry that the store would refuse is not a relationship: the same checks as Add, cap included.
            // A file written before URLs were kept in lower case may hold mixed-case ones: those are lower-cased (and
            // deduped, the first kept), not dropped.
            if (!IsSessionId(id)) continue;
            var valid = (list ?? []).Where(p => p?.Url is not null && p.Source is FromBlock or FromBranch or FromStack)
                .Select(p => p with { Url = PullRequestUrl.Canonical(p.Url) })
                .Where(p => IsStorable(p.Url))
                .DistinctBy(p => p.Url, StringComparer.Ordinal).ToList();
            var blocks = valid.Where(p => p.Source == FromBlock).Skip(MaxBlockPerSession).ToHashSet();
            valid = valid.Where(p => !blocks.Contains(p)).ToList();
            if (valid.Count > 0) sessions[id] = valid;
        }
        return _store = new Store(read?.ReapedAt, sessions);
    }

    private void Save(Store store)
    {
        Directory.CreateDirectory(paths.HubDir);
        AtomicFile.Write(_file, JsonSerializer.Serialize(store, Json.Options));
        _store = store;
    }
}

using System.Text.Json;
using System.Text.RegularExpressions;
using Fyrd.Core.CloudSessions;

namespace Fyrd.Core.Hub;

/// <summary>
/// One hub poll pass: captures <c>fyrd-remember</c> and <c>fyrd-prs</c> blocks from hub sessions, finds pending
/// <c>AskUserQuestion</c> items, publishes the session list to <see cref="HubState"/>, and posts memory updates to the
/// Controller. A peasant's <c>scope: shared</c> block goes to <see cref="PendingMemory"/> instead of shared memory; a
/// <c>fyrd-prs</c> block's PRs and the session's own PRs among those found on its branches (see <c>RememberOwned</c>)
/// go to <see cref="SessionPullRequests"/>.
/// Passes must run sequentially (the per-session windows and the cursor file are not locked).
/// </summary>
public sealed partial class HubRunner(
    ICloudSessions cloud, HubMemory memory, HubPersonas personas, ControllerManager controller,
    HubState state, FyrdPaths paths, TimeProvider time, CostLedger ledger, CostGuard guard, PendingMemory pending,
    IOwnerMessages owner, OwnedPullRequests? ownedPrs = null, NotificationDismissals? dismissals = null,
    SessionPullRequests? sessionPrs = null)
{
    private const string HubTag = "fyrd-hub";
    private const string RoleTagPrefix = "fyrd-role-";
    private const string ControllerRole = "controller";
    private const string RequiresAction = "requires_action";
    private const int RecentEvents = 200;
    private const int PageLimit = 100;
    /// <summary>History reads in flight at once; the first pass after a start reads every session's whole history.</summary>
    private const int FetchConcurrency = 8;
    /// <summary>Sessions whose reads may be started ahead of the one being processed (bounds buffered history).</summary>
    private const int ReadAhead = 16;
    /// <summary>Repos listed on GitHub at once.</summary>
    private const int OwnedConcurrency = 4;
    /// <summary>On the first pass, sessions processed between progressive publishes.</summary>
    private const int PublishEvery = 10;

    /// <summary>In-memory per-session tail used for question scanning; rebuilt from the start after a restart.</summary>
    private sealed class Window
    {
        public string? After;
        public List<SessionEvent> Recent = [];
        public DateTimeOffset? LastActivity;
    }

    private readonly Dictionary<string, Window> _windows = new(StringComparer.Ordinal);
    private readonly string _cursorFile = Path.Combine(paths.HubDir, "cursors.json");
    private readonly SummaryLog _summaries = new(paths, time);
    private Dictionary<string, string>? _cursors;
    private string? _controllerId;
    private readonly NotificationDismissals _dismissals = dismissals ?? new(paths);
    private readonly SessionPullRequests _sessionPrs = sessionPrs ?? new(paths, time);

    [GeneratedRegex("^[a-z0-9-]{1,40}$", RegexOptions.CultureInvariant)]
    private static partial Regex ScopeRegex();

    public async Task PollOnceAsync(CancellationToken ct)
    {
        try
        {
            await PassAsync(ct);
        }
        catch (Exception e) when (e is CloudSessionAuthException or ClaudeAuthMissingException)
        {
            state.SignedOut = true;
            state.LastPollAt = time.GetUtcNow();
        }
    }

    private async Task PassAsync(CancellationToken ct)
    {
        string? lastError = null;
        var sessions = (await cloud.ListByTagAsync(HubTag, ct))
            .Where(s => s.SessionStatus != "archived")
            .ToList();
        var previousViews = state.Sessions.ToDictionary(v => v.Id, StringComparer.Ordinal);
        var previousQuestions = state.Questions.ToLookup(q => q.SessionId, StringComparer.Ordinal);
        var previousNotifications = state.Notifications.ToLookup(n => n.SessionId, StringComparer.Ordinal);

        var views = new List<HubSessionView>();
        var questions = new List<HubQuestion>();
        var notifications = new List<HubNotification>();
        var failed = new HashSet<string>(StringComparer.Ordinal);

        // The owner creates the Controller on claude.ai, so it carries no fyrd-hub tag: fetch it by its stored id.
        _controllerId = controller.CurrentId;
        if (_controllerId is { } cid && sessions.All(s => s.Id != cid))
        {
            try
            {
                var c = await cloud.GetAsync(cid, ct);
                if (c.SessionStatus != "archived") sessions.Add(c);
            }
            catch (CloudSessionApiException e) when (e.Status == 404) { }
            catch (Exception e) when (e is not (CloudSessionAuthException or ClaudeAuthMissingException)
                                      && (e is not OperationCanceledException || !ct.IsCancellationRequested))
            {
                // Unknown liveness: keep what the last pass showed, and do not post memory to it.
                lastError ??= $"{cid}: {e.Message}";
                failed.Add(cid);
                if (previousViews.TryGetValue(cid, out var old)) views.Add(old);
                questions.AddRange(previousQuestions[cid]);
                notifications.AddRange(previousNotifications[cid]);
            }
        }

        // Nothing to show yet (first pass after a start): publish the bare list now, so the console does not wait for
        // every session's history to be read. Later passes keep showing the previous views until they finish.
        var firstPass = previousViews.Count == 0 && sessions.Count > 0;
        if (firstPass)
            state.Publish(sessions.Select(s => View(s, null)).ToList(), [], null, state.Budget);

        // The reads are the slow part and independent, so they overlap; everything that touches shared state stays sequential.
        using var fetchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var fetches = new Fetches(this, sessions, fetchCts.Token);
        var ownedWarm = WarmOwned(sessions, fetchCts.Token);
        try
        {
            for (var i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                ct.ThrowIfCancellationRequested();
                _summaries.RecordPolled(s.Id, Title(s), s.Raw);
                try
                {
                    var w = await PollSessionAsync(s, fetches.Take(s), ct);
                    var (repo, _) = RepoAndBranch(s.Raw);
                    var owned = OwnedSet.Empty;
                    if (ownedPrs is not null)
                    {
                        // The repo's listing is fetched once, up front and concurrently; wait for it so it is not fetched twice.
                        if (repo is not null && ownedWarm.TryGetValue(repo, out var warm)) await warm;
                        owned = await ownedPrs.FindOwnedAsync(repo, Branches(s.Raw), ct);
                        RememberOwned(s, owned);
                    }
                    views.Add(View(s, w.LastActivity, owned));
                    // A can_use_tool left unanswered by an interrupt stays in the log; only a session that is actually
                    // waiting on the owner has a live question.
                    if (s.SessionStatus == RequiresAction)
                        questions.AddRange(PendingQuestions.Find(s.Id, Title(s), w.Recent));
                    else if (HubNotifications.Find(s, Title(s), w.Recent) is { } n && !_dismissals.Contains(n.Id))
                        notifications.Add(n);
                }
                catch (Exception e) when (e is not (CloudSessionAuthException or ClaudeAuthMissingException)
                                          && (e is not OperationCanceledException || !ct.IsCancellationRequested))
                {
                    lastError ??= $"{s.Id}: {e.Message}";
                    failed.Add(s.Id);
                    views.Add(previousViews.TryGetValue(s.Id, out var old) ? old : View(s, null));
                    questions.AddRange(previousQuestions[s.Id]);
                    notifications.AddRange(previousNotifications[s.Id]);
                }

                // First pass only: let rows fill in as they are read instead of all at the end.
                if (firstPass && (i + 1) % PublishEvery == 0 && i + 1 < sessions.Count)
                    state.Publish(views.Concat(sessions.Skip(i + 1).Select(r => View(r, null))).ToList(),
                        questions.OrderBy(q => q.AskedAt).ToList(), null, state.Budget);
            }
        }
        finally
        {
            // An aborted pass (sign-out, shutdown) must not leave reads running or their failures unobserved.
            fetchCts.Cancel();
            fetches.Observe();
            foreach (var t in ownedWarm.Values) _ = t.ContinueWith(x => _ = x.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }

        Prune(sessions.Select(s => s.Id).Concat(failed).ToHashSet(StringComparer.Ordinal));

        // Views carry costs when published; interrupts and Controller messages come after, so slow calls never delay them.
        CostPass? costPass = null;
        var costLive = views.Select(v => (v.Id, v.Title)).ToList();
        try
        {
            costPass = guard.Build(costLive.Select(l => l.Id).ToList());
            views = views.Select(v => costPass.Views.TryGetValue(v.Id, out var cost) ? v with { Cost = cost } : v).ToList();
        }
        catch (Exception e) when (e is not (CloudSessionAuthException or ClaudeAuthMissingException)
                                  && (e is not OperationCanceledException || !ct.IsCancellationRequested))
        {
            lastError ??= $"budget: {e.Message}";
            views = views.Select(v => v.Cost is null && previousViews.TryGetValue(v.Id, out var old) ? v with { Cost = old.Cost } : v).ToList();
        }

        state.Publish(views, questions.OrderBy(q => q.AskedAt).ToList(), failed, costPass?.Budget ?? state.Budget, PullRequestStatuses(views));
        state.PublishNotifications(notifications);

        // Separate tries: a failed or slow instructions refresh must not skip the memory update for the pass.
        var liveIds = sessions.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        // A session this pass could not read is alive as far as anyone knows: only the cloud's own answer reaps one.
        var reapLive = liveIds.Concat(failed).ToHashSet(StringComparer.Ordinal);
        foreach (var post in new Func<Task>[]
                 {
                     () => PostInstructionsRefreshAsync(sessions, ct), () => PostMemoryUpdateAsync(liveIds, ct),
                     () => _sessionPrs.ReapAsync(cloud, reapLive, ct),
                 })
        {
            try { await post(); }
            catch (Exception e) when (e is not (CloudSessionAuthException or ClaudeAuthMissingException)
                                      && (e is not OperationCanceledException || !ct.IsCancellationRequested))
            {
                lastError ??= $"controller update: {e.Message}";
            }
        }

        if (costPass is not null)
        {
            try
            {
                var controllerLive = _controllerId is { } c && sessions.Any(x => x.Id == c) && !failed.Contains(c);
                lastError ??= await guard.EnforceAsync(costLive, costPass, controllerLive, ct);
            }
            catch (Exception e) when (e is not (CloudSessionAuthException or ClaudeAuthMissingException)
                                      && (e is not OperationCanceledException || !ct.IsCancellationRequested))
            {
                lastError ??= $"budget: {e.Message}";
            }
        }

        state.SignedOut = false;
        state.LastError = lastError;
        state.LastPollAt = time.GetUtcNow();
    }

    /// <summary>
    /// Lists each distinct repo's pull requests once, concurrently, so the sequential loop only waits on a repo's own listing
    /// (which <see cref="OwnedPullRequests"/> then caches) instead of paying one GitHub round trip per repo in turn.
    /// A failure here is left for the loop's own call to hit and report.
    /// </summary>
    private Dictionary<string, Task> WarmOwned(IReadOnlyList<CloudSession> sessions, CancellationToken ct)
    {
        var warm = new Dictionary<string, Task>(StringComparer.OrdinalIgnoreCase);
        if (ownedPrs is null) return warm;
        var gate = new SemaphoreSlim(OwnedConcurrency);
        foreach (var s in sessions)
        {
            var (repo, _) = RepoAndBranch(s.Raw);
            if (repo is null || warm.ContainsKey(repo)) continue;
            var branches = Branches(s.Raw);
            warm[repo] = Task.Run(async () =>
            {
                await gate.WaitAsync(ct);
                try { await ownedPrs.FindOwnedAsync(repo, branches, ct); }
                catch (Exception) when (!ct.IsCancellationRequested) { }
                finally { gate.Release(); }
            }, ct);
        }
        return warm;
    }

    /// <summary>
    /// The sessions' event reads: at most <see cref="FetchConcurrency"/> in flight and at most <see cref="ReadAhead"/>
    /// sessions ahead of the one being processed, so a long list does not buffer every history at once. A result is
    /// released once taken.
    /// </summary>
    private sealed class Fetches
    {
        private readonly HubRunner _runner;
        private readonly IReadOnlyList<CloudSession> _sessions;
        private readonly CancellationToken _ct;
        private readonly SemaphoreSlim _gate = new(FetchConcurrency);
        private readonly Dictionary<string, Task<List<SessionEvent>>> _tasks = new(StringComparer.Ordinal);
        private int _started;
        private int _taken;

        public Fetches(HubRunner runner, IReadOnlyList<CloudSession> sessions, CancellationToken ct)
        {
            (_runner, _sessions, _ct) = (runner, sessions, ct);
            Pump(0);
        }

        private void Pump(int consumed)
        {
            while (_started < _sessions.Count && _started < consumed + ReadAhead)
            {
                var id = _sessions[_started++].Id;
                if (_tasks.ContainsKey(id)) continue; // a duplicate id shares the first read
                var after = _runner._windows.TryGetValue(id, out var w) ? w.After : null;
                _tasks.Add(id, Task.Run(async () =>
                {
                    await _gate.WaitAsync(_ct);
                    try { return await _runner.FetchAfterAsync(id, after, _ct); }
                    finally { _gate.Release(); }
                }, _ct));
            }
        }

        public Task<List<SessionEvent>> Take(CloudSession s)
        {
            Pump(++_taken);
            if (_tasks.Remove(s.Id, out var t)) return t;
            // Only a duplicate id gets here: its first read was already taken.
            return _runner.FetchAfterAsync(s.Id, _runner._windows.TryGetValue(s.Id, out var w) ? w.After : null, _ct);
        }

        /// <summary>Marks every read still held as observed, so an abandoned one never raises an unobserved fault.</summary>
        public void Observe()
        {
            foreach (var t in _tasks.Values) _ = t.ContinueWith(x => _ = x.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    private async Task<Window> PollSessionAsync(CloudSession s, Task<List<SessionEvent>> fetch, CancellationToken ct)
    {
        _windows.TryGetValue(s.Id, out var w);
        var firstSight = w is null;
        var fetched = await fetch;

        // Memory capture follows the persisted cursor. After a restart the window starts over from the beginning,
        // so skip everything up to the stored cursor; if that event is gone, capture nothing rather than duplicate.
        IEnumerable<SessionEvent> fresh = fetched;
        var cursors = Cursors();
        if (firstSight && cursors.TryGetValue(s.Id, out var cursor))
        {
            var idx = fetched.FindIndex(e => e.Uuid == cursor);
            fresh = idx >= 0 ? fetched.Skip(idx + 1) : [];
        }
        Capture(s, fresh, cursors);
        ledger.Feed(s.Id, fetched);
        ledger.Observe(s.Id, s.Raw);
        if (fetched.Count > 0 && fetched[^1].Uuid is { Length: > 0 } last && (!cursors.TryGetValue(s.Id, out var c) || c != last))
        {
            cursors[s.Id] = last;
            SaveCursors();
        }

        w ??= new Window();
        if (fetched.Count > 0)
        {
            w.Recent.AddRange(fetched);
            if (w.Recent.Count > RecentEvents) w.Recent.RemoveRange(0, w.Recent.Count - RecentEvents);
            w.After = fetched[^1].Uuid;
            var newest = fetched.Max(e => e.CreatedAt);
            if (newest != default && (w.LastActivity is null || newest > w.LastActivity)) w.LastActivity = newest;
        }
        _windows[s.Id] = w;
        return w;
    }

    private async Task<List<SessionEvent>> FetchAfterAsync(string sessionId, string? after, CancellationToken ct)
    {
        var events = new List<SessionEvent>();
        await foreach (var e in cloud.AllEventsAsync(sessionId, after, PageLimit, ct)) events.Add(e);
        return events;
    }

    /// <summary>
    /// Persists the cursor after each event that added memory, queued a proposal or added a PR relationship, so a later
    /// failure never re-adds it (re-adding a PR would be harmless anyway: the cache is a set).
    /// </summary>
    private void Capture(CloudSession s, IEnumerable<SessionEvent> events, Dictionary<string, string> cursors)
    {
        var role = Role(s);
        var isController = IsController(s);
        var source = Title(s);
        foreach (var e in events)
        {
            if (e.Type != "assistant" || string.IsNullOrEmpty(e.Text)) continue;
            var consumed = false;
            foreach (var d in RememberBlock.Parse(e.Text))
            {
                var at = e.CreatedAt == default ? time.GetUtcNow() : e.CreatedAt;
                if (d.Scope == "shared" && !isController)
                {
                    // A peasant's shared memory reaches every session, so it waits for the owner's approval.
                    pending.Add(d.Text, source, s.Id, at);
                    consumed = true;
                    continue;
                }
                string? scope = d.Scope == "shared" || isController ? HubMemory.Shared
                    : role is not null && role != HubMemory.Shared && ScopeRegex().IsMatch(role) ? role
                    : null;
                if (scope is null) continue;
                memory.Add(scope, d.Text, source, at);
                consumed = true;
            }
            // Only the session's own reply names its PRs: a subagent's or worker's message (it may be about any branch) does not.
            var prs = HubNotifications.IsSubagent(e) ? [] : PullRequestBlock.Parse(e.Text);
            if (prs.Count > 0 && _sessionPrs.Add(s.Id, prs, SessionPullRequests.FromBlock, e.CreatedAt == default ? null : e.CreatedAt).Count > 0)
                consumed = true;
            if (consumed && e.Uuid.Length > 0)
            {
                cursors[s.Id] = e.Uuid;
                SaveCursors();
            }
        }
    }

    /// <summary>
    /// Re-sends the full Controller message when its instructions (definition, settings, personas, skills) changed, so
    /// edits to skills and personas reach the live Controller. Only while it is live and idle; otherwise a later pass
    /// does it. The message carries memory too, so both hashes advance. Never posts to an archived or replaced Controller.
    /// </summary>
    private async Task PostInstructionsRefreshAsync(IReadOnlyList<CloudSession> sessions, CancellationToken ct)
    {
        if (_controllerId is not { } controllerId) return;
        var session = sessions.FirstOrDefault(s => s.Id == controllerId);
        if (session is null || session.SessionStatus is "running" or RequiresAction) return;
        if (controller.SkillsMissing) return;
        var (prompt, instructionsHash, memoryHash) = controller.BuildPrompt();
        // A new Controller created during this pass replaced the old one: post nothing to the old one.
        if (controller.CurrentId != controllerId || instructionsHash == controller.PostedInstructionsHash) return;
        // Sent as the owner's own turn: a running session rejects a peer-wrapped message for this.
        await owner.SendAsync(controllerId, prompt, ct);
        controller.MarkPosted(instructionsHash, memoryHash);
    }

    /// <summary>Posts only to the Controller this pass read as live; an archived or unreadable one is never written to.</summary>
    private async Task PostMemoryUpdateAsync(IReadOnlySet<string> live, CancellationToken ct)
    {
        if (_controllerId is not { } controllerId || !live.Contains(controllerId)) return;
        var text = HubPrompt.MemoryUpdate(memory, personas.List());
        // Hashed over the exact text sent, so a concurrent memory edit cannot be marked as posted.
        var hash = ControllerManager.Hash(text);
        // A new Controller created during this pass replaced the old one (and set its own hash): post nothing to the old one.
        if (controller.CurrentId != controllerId || hash == controller.PostedMemoryHash) return;
        // Same path as a typed message: a running session rejects a peer-wrapped memory update.
        await owner.SendAsync(controllerId, text, ct);
        controller.PostedMemoryHash = hash;
    }

    /// <summary>Drops in-memory windows only. Persisted memory cursors are never pruned: a session that is archived and
    /// restored, or missing from one listing, must not have its history captured again.</summary>
    private void Prune(HashSet<string> live)
    {
        foreach (var id in _windows.Keys.Where(id => !live.Contains(id)).ToList()) _windows.Remove(id);
    }

    /// <summary>
    /// Caches the PRs the branch matching found that are this session's own: a relationship stays once it exists. A direct
    /// match is by branch name only, so it is never cached when its head is in a fork (a fork's branch merely shares the
    /// name) or is the repo's default branch (a session on <c>main</c> matches every PR from <c>main</c>), or when the
    /// listing did not say which branch is the default. Branch names are also reused (a recycled peasant keeps
    /// <c>fyrd/&lt;title&gt;</c>), so any other direct match is cached only while it is open, or when it was opened at or
    /// after the session was created; a merged or closed PR from before the session, or one with no known creation time, is
    /// shown while the matching finds it but never cached. Stack matches are always open same-repo PRs (the walk ignores
    /// merged, closed and fork PRs) and are cached.
    /// </summary>
    private void RememberOwned(CloudSession s, OwnedSet owned)
    {
        if (owned.Urls.Count == 0) return;
        var stacked = owned.Stacked.ToHashSet(StringComparer.Ordinal);
        var direct = owned.Direct.ToDictionary(p => p.Url, StringComparer.Ordinal);
        var sessionCreated = CreatedAt(s.Raw);
        // One at a time, in the listing's order, so the cache keeps that order; only a new PR writes the file.
        foreach (var url in owned.Urls)
        {
            if (stacked.Contains(url))
                _sessionPrs.Add(s.Id, [url], SessionPullRequests.FromStack);
            else if (direct.TryGetValue(url, out var pr) && OwnDirectMatch(pr, owned.DefaultBranch, sessionCreated))
                _sessionPrs.Add(s.Id, [url], SessionPullRequests.FromBranch);
        }
    }

    private static bool OwnDirectMatch(PullRequestHead pr, string? defaultBranch, DateTimeOffset? sessionCreated) =>
        !pr.CrossRepository && !string.IsNullOrEmpty(defaultBranch) && pr.Branch != defaultBranch
        && (pr.State == "open" || (pr.CreatedAt is { } opened && sessionCreated is { } created && opened >= created));

    /// <summary>
    /// A session's pull requests: the ones found now on its repo and branches (the cloud API reports none itself), then
    /// every other one cached for it (<see cref="SessionPullRequests"/>: named in a <c>fyrd-prs</c> block, or found on its
    /// branches before), oldest first. A cached PR the matching no longer finds counts as stacked when it was cached as one.
    /// </summary>
    private HubSessionView View(CloudSession s, DateTimeOffset? lastActivity, OwnedSet? owned = null)
    {
        var (repo, branch) = RepoAndBranch(s.Raw);
        var urls = (owned?.Urls ?? []).ToList();
        var stacked = (owned?.Stacked ?? []).ToList();
        foreach (var cached in _sessionPrs.For(s.Id))
        {
            if (urls.Contains(cached.Url, StringComparer.OrdinalIgnoreCase)) continue;
            urls.Add(cached.Url);
            if (cached.Source == SessionPullRequests.FromStack) stacked.Add(cached.Url);
        }
        return new HubSessionView(s.Id, Title(s), Role(s) ?? "", s.SessionStatus ?? "unknown", repo, branch,
            CreatedAt(s.Raw), lastActivity, s.Tags.ToList())
        {
            Repos = Repos(s.Raw),
            Branches = Branches(s.Raw),
            PullRequests = urls,
            StackedPullRequests = stacked,
        };
    }

    /// <summary>The listed status of every PR any session owns, one entry per URL.</summary>
    private IReadOnlyDictionary<string, PullRequestStatus> PullRequestStatuses(IEnumerable<HubSessionView> views)
    {
        var map = new Dictionary<string, PullRequestStatus>(StringComparer.Ordinal);
        if (ownedPrs is null) return map;
        foreach (var url in views.SelectMany(v => v.PullRequests).Distinct(StringComparer.Ordinal))
            if (ownedPrs.Status(url) is { } status) map[url] = status;
        return map;
    }

    private bool IsController(CloudSession s) => s.Id == _controllerId;

    private string Title(CloudSession s) =>
        !string.IsNullOrEmpty(s.Title) ? s.Title : IsController(s) ? ControllerRole : s.Id;

    /// <summary>The Controller is identified by its stored id only (it is created through the CLI and has no role tag); others by their role tag, which can never be <c>controller</c>.</summary>
    private string? Role(CloudSession s) =>
        IsController(s) ? ControllerRole
        : s.Tags.FirstOrDefault(t => t.StartsWith(RoleTagPrefix, StringComparison.Ordinal))?[RoleTagPrefix.Length..] is { } tagged
          && tagged != ControllerRole ? tagged : null; // a role tag never makes another session the Controller

    private static DateTimeOffset? CreatedAt(JsonElement raw) =>
        Str(raw, "created_at") is { } v && DateTimeOffset.TryParse(v, out var at) ? at : null;

    private static string RepoName(string url)
    {
        const string gh = "https://github.com/";
        var repo = url.StartsWith(gh, StringComparison.OrdinalIgnoreCase) ? url[gh.Length..].TrimEnd('/') : url;
        return repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? repo[..^4] : repo;
    }

    /// <summary>Every attached repo, in source order, without duplicates.</summary>
    private static IReadOnlyList<string> Repos(JsonElement raw)
    {
        var list = new List<string>();
        if (raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("session_context", out var ctx)
            && ctx.ValueKind == JsonValueKind.Object && ctx.TryGetProperty("sources", out var sources)
            && sources.ValueKind == JsonValueKind.Array)
            foreach (var src in sources.EnumerateArray())
                if (Str(src, "url") is { Length: > 0 } url) list.Add(RepoName(url));
        return list.Distinct(StringComparer.Ordinal).ToList();
    }

    private static (string? Repo, string? Branch) RepoAndBranch(JsonElement raw)
    {
        var ctx = raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("session_context", out var c) ? c : default;
        string? repo = null;
        if (First(ctx, "sources") is { } src && Str(src, "url") is { Length: > 0 } url)
            repo = RepoName(url);
        // The live branch comes first: builders' outcomes are empty, so it is usually the only source.
        var branch = LiveBranch(raw);
        if (branch is null && First(ctx, "outcomes") is { } outcome
            && outcome.TryGetProperty("git_info", out var gi) && gi.ValueKind == JsonValueKind.Object
            && First(gi, "branches") is { ValueKind: JsonValueKind.String } b)
            branch = b.GetString();
        return (repo, branch);
    }

    /// <summary>The first non-empty value of <c>external_metadata.current_branches</c> (keyed by mount path).</summary>
    private static string? LiveBranch(JsonElement raw) => LiveBranches(raw).FirstOrDefault();

    private static IEnumerable<string> LiveBranches(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object || !raw.TryGetProperty("external_metadata", out var em)
            || em.ValueKind != JsonValueKind.Object || !em.TryGetProperty("current_branches", out var cb)
            || cb.ValueKind != JsonValueKind.Object)
            yield break;
        foreach (var p in cb.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 } name) yield return name;
    }

    /// <summary>Every branch the session has: all live ones (one per mount) and all its outcomes' branches.</summary>
    private static IReadOnlyList<string> Branches(JsonElement raw)
    {
        var all = LiveBranches(raw).ToList();
        if (raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("session_context", out var ctx)
            && ctx.ValueKind == JsonValueKind.Object && ctx.TryGetProperty("outcomes", out var outcomes)
            && outcomes.ValueKind == JsonValueKind.Array)
            foreach (var o in outcomes.EnumerateArray())
                if (o.ValueKind == JsonValueKind.Object && o.TryGetProperty("git_info", out var gi) && gi.ValueKind == JsonValueKind.Object
                    && gi.TryGetProperty("branches", out var bs) && bs.ValueKind == JsonValueKind.Array)
                    foreach (var b in bs.EnumerateArray())
                        if (b.ValueKind == JsonValueKind.String && b.GetString() is { Length: > 0 } name) all.Add(name);
        return all.Distinct(StringComparer.Ordinal).ToList();
    }

    private static JsonElement? First(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var arr)
        && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0
            ? arr[0] : null;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private Dictionary<string, string> Cursors()
    {
        if (_cursors is not null) return _cursors;
        try
        {
            if (File.Exists(_cursorFile))
                _cursors = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_cursorFile), Json.Options);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { }
        return _cursors ??= new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private void SaveCursors() => AtomicFile.Write(_cursorFile, JsonSerializer.Serialize(Cursors(), Json.Options));
}

using System.Text.Json;
using System.Text.RegularExpressions;
using Fyrd.Core.CloudSessions;

namespace Fyrd.Core.Hub;

/// <summary>
/// One hub poll pass: captures <c>fyrd-remember</c> blocks from hub sessions, finds pending <c>AskUserQuestion</c>
/// items, publishes the session list to <see cref="HubState"/>, and posts memory updates to the Controller.
/// Passes must run sequentially (the per-session windows and the cursor file are not locked).
/// </summary>
public sealed partial class HubRunner(
    ICloudSessions cloud, HubMemory memory, HubPersonas personas, ControllerManager controller,
    HubState state, FyrdPaths paths, TimeProvider time)
{
    private const string HubTag = "fyrd-hub";
    private const string RoleTagPrefix = "fyrd-role-";
    private const string ControllerRole = "controller";
    private const string RequiresAction = "requires_action";
    private const int RecentEvents = 200;
    private const int PageLimit = 100;

    /// <summary>In-memory per-session tail used for question scanning; rebuilt from the start after a restart.</summary>
    private sealed class Window
    {
        public string? After;
        public List<SessionEvent> Recent = [];
        public DateTimeOffset? LastActivity;
    }

    private readonly Dictionary<string, Window> _windows = new(StringComparer.Ordinal);
    private readonly string _cursorFile = Path.Combine(paths.HubDir, "cursors.json");
    private Dictionary<string, string>? _cursors;
    private string? _controllerId;

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

        var views = new List<HubSessionView>();
        var questions = new List<HubQuestion>();
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
            }
        }
        foreach (var s in sessions)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var w = await PollSessionAsync(s, ct);
                views.Add(View(s, w.LastActivity));
                // A can_use_tool left unanswered by an interrupt stays in the log; only a session that is actually
                // waiting on the owner has a live question.
                if (s.SessionStatus == RequiresAction)
                    questions.AddRange(PendingQuestions.Find(s.Id, Title(s), w.Recent));
            }
            catch (Exception e) when (e is not (CloudSessionAuthException or ClaudeAuthMissingException)
                                      && (e is not OperationCanceledException || !ct.IsCancellationRequested))
            {
                lastError ??= $"{s.Id}: {e.Message}";
                failed.Add(s.Id);
                views.Add(previousViews.TryGetValue(s.Id, out var old) ? old : View(s, null));
                questions.AddRange(previousQuestions[s.Id]);
            }
        }

        Prune(sessions.Select(s => s.Id).Concat(failed).ToHashSet(StringComparer.Ordinal));
        state.Publish(views, questions.OrderBy(q => q.AskedAt).ToList(), failed);

        try
        {
            await PostMemoryUpdateAsync(sessions.Select(s => s.Id).ToHashSet(StringComparer.Ordinal), ct);
        }
        catch (Exception e) when (e is not (CloudSessionAuthException or ClaudeAuthMissingException)
                                  && (e is not OperationCanceledException || !ct.IsCancellationRequested))
        {
            lastError ??= $"memory update: {e.Message}";
        }

        state.SignedOut = false;
        state.LastError = lastError;
        state.LastPollAt = time.GetUtcNow();
    }

    private async Task<Window> PollSessionAsync(CloudSession s, CancellationToken ct)
    {
        _windows.TryGetValue(s.Id, out var w);
        var firstSight = w is null;
        var fetched = await FetchAfterAsync(s.Id, w?.After, ct);

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
        while (true)
        {
            var page = await cloud.ListEventsAsync(sessionId, PageLimit, after, null, ct);
            events.AddRange(page.Events);
            if (!page.HasMore || page.LastId is null || page.LastId == after) break;
            after = page.LastId;
        }
        return events;
    }

    /// <summary>Persists the cursor after each event that added memory, so a later failure never re-adds it.</summary>
    private void Capture(CloudSession s, IEnumerable<SessionEvent> events, Dictionary<string, string> cursors)
    {
        var role = Role(s);
        var isController = role == ControllerRole;
        var source = Title(s);
        foreach (var e in events)
        {
            if (e.Type != "assistant" || string.IsNullOrEmpty(e.Text)) continue;
            var added = false;
            foreach (var d in RememberBlock.Parse(e.Text))
            {
                string? scope = d.Scope == "shared" || isController ? HubMemory.Shared
                    : role is not null && role != HubMemory.Shared && ScopeRegex().IsMatch(role) ? role
                    : null;
                if (scope is null) continue;
                memory.Add(scope, d.Text, source, e.CreatedAt == default ? time.GetUtcNow() : e.CreatedAt);
                added = true;
            }
            if (added && e.Uuid.Length > 0)
            {
                cursors[s.Id] = e.Uuid;
                SaveCursors();
            }
        }
    }

    /// <summary>Posts only to the Controller this pass read as live; an archived or unreadable one is never written to.</summary>
    private async Task PostMemoryUpdateAsync(IReadOnlySet<string> live, CancellationToken ct)
    {
        if (_controllerId is not { } controllerId || !live.Contains(controllerId)) return;
        var text = HubPrompt.MemoryUpdate(memory, personas.List());
        // Hashed over the exact text sent, so a concurrent memory edit cannot be marked as posted.
        var hash = ControllerManager.Hash(text);
        // An adoption during this pass replaced the Controller (and set its own hash): post nothing to the old one.
        if (controller.CurrentId != controllerId || hash == controller.PostedMemoryHash) return;
        await cloud.SendMessageAsync(controllerId, text, ct);
        controller.PostedMemoryHash = hash;
    }

    /// <summary>Drops in-memory windows only. Persisted memory cursors are never pruned: a session that is archived and
    /// restored, or missing from one listing, must not have its history captured again.</summary>
    private void Prune(HashSet<string> live)
    {
        foreach (var id in _windows.Keys.Where(id => !live.Contains(id)).ToList()) _windows.Remove(id);
    }

    private HubSessionView View(CloudSession s, DateTimeOffset? lastActivity)
    {
        var (repo, branch) = RepoAndBranch(s.Raw);
        return new HubSessionView(s.Id, Title(s), Role(s) ?? "", s.SessionStatus ?? "unknown", repo, branch,
            CreatedAt(s.Raw), lastActivity, s.Tags.ToList()) { PullRequests = PullRequests(s.Raw) };
    }

    private bool IsController(CloudSession s) => s.Id == _controllerId;

    private string Title(CloudSession s) =>
        !string.IsNullOrEmpty(s.Title) ? s.Title : IsController(s) ? ControllerRole : s.Id;

    /// <summary>The Controller is identified by its stored id (an adopted one has no role tag); others by their role tag.</summary>
    private string? Role(CloudSession s) =>
        IsController(s) ? ControllerRole
        : s.Tags.FirstOrDefault(t => t.StartsWith(RoleTagPrefix, StringComparison.Ordinal))?[RoleTagPrefix.Length..];

    private static DateTimeOffset? CreatedAt(JsonElement raw) =>
        Str(raw, "created_at") is { } v && DateTimeOffset.TryParse(v, out var at) ? at : null;

    private static (string? Repo, string? Branch) RepoAndBranch(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object || !raw.TryGetProperty("session_context", out var ctx)
            || ctx.ValueKind != JsonValueKind.Object)
            return (null, null);
        string? repo = null, branch = null;
        if (First(ctx, "sources") is { } src && Str(src, "url") is { Length: > 0 } url)
        {
            const string gh = "https://github.com/";
            repo = url.StartsWith(gh, StringComparison.OrdinalIgnoreCase) ? url[gh.Length..].TrimEnd('/') : url;
            if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) repo = repo[..^4];
        }
        if (First(ctx, "outcomes") is { } outcome
            && outcome.TryGetProperty("git_info", out var gi) && gi.ValueKind == JsonValueKind.Object
            && First(gi, "branches") is { ValueKind: JsonValueKind.String } b)
            branch = b.GetString();
        return (repo, branch);
    }

    /// <summary>
    /// Pull request URLs in the session's outcomes' <c>git_info</c>: a <c>pull_requests</c> array (URL strings or objects
    /// with a <c>url</c>) and/or a single <c>pr_url</c> / <c>pull_request_url</c>. Distinct, in order seen.
    /// </summary>
    private static IReadOnlyList<string> PullRequests(JsonElement raw)
    {
        var urls = new List<string>();
        void Add(string? u) { if (!string.IsNullOrWhiteSpace(u) && !urls.Contains(u)) urls.Add(u); }
        if (raw.ValueKind != JsonValueKind.Object || !raw.TryGetProperty("session_context", out var ctx)
            || ctx.ValueKind != JsonValueKind.Object || !ctx.TryGetProperty("outcomes", out var outcomes)
            || outcomes.ValueKind != JsonValueKind.Array)
            return urls;
        foreach (var o in outcomes.EnumerateArray())
        {
            if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty("git_info", out var gi) || gi.ValueKind != JsonValueKind.Object)
                continue;
            if (gi.TryGetProperty("pull_requests", out var prs) && prs.ValueKind == JsonValueKind.Array)
                foreach (var pr in prs.EnumerateArray())
                    Add(pr.ValueKind == JsonValueKind.String ? pr.GetString() : Str(pr, "url"));
            Add(Str(gi, "pr_url"));
            Add(Str(gi, "pull_request_url"));
        }
        return urls;
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

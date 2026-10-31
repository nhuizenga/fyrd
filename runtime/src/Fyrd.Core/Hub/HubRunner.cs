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
    private const string ControllerTag = "fyrd-controller";
    private const string ControllerRole = "controller";
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
        foreach (var s in sessions)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var w = await PollSessionAsync(s, ct);
                views.Add(View(s, w.LastActivity));
                questions.AddRange(PendingQuestions.Find(s.Id, Title(s), w.Recent));
            }
            catch (Exception e) when (e is not (CloudSessionAuthException or ClaudeAuthMissingException)
                                      && (e is not OperationCanceledException || !ct.IsCancellationRequested))
            {
                lastError ??= $"{s.Id}: {e.Message}";
                views.Add(previousViews.TryGetValue(s.Id, out var old) ? old : View(s, null));
                questions.AddRange(previousQuestions[s.Id]);
            }
        }

        Prune(sessions.Select(s => s.Id).ToHashSet(StringComparer.Ordinal));
        state.PublishRecent(_windows.ToDictionary(
            kv => kv.Key, kv => (IReadOnlyList<SessionEvent>)kv.Value.Recent.ToArray(), StringComparer.Ordinal));
        state.Publish(views, questions.OrderBy(q => q.AskedAt).ToList());

        try
        {
            await PostMemoryUpdateAsync(ct);
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
        var isController = s.Tags.Contains(ControllerTag) || role == ControllerRole;
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

    private async Task PostMemoryUpdateAsync(CancellationToken ct)
    {
        if (controller.CurrentId is not { } controllerId) return;
        var text = HubPrompt.MemoryUpdate(memory, personas.List());
        // Hashed over the exact text sent, so a concurrent memory edit cannot be marked as posted.
        var hash = ControllerManager.Hash(text);
        if (hash == controller.PostedMemoryHash) return;
        await cloud.SendMessageAsync(controllerId, text, ct);
        controller.PostedMemoryHash = hash;
    }

    /// <summary>Drops in-memory windows only. Persisted memory cursors are never pruned: a session that is archived and
    /// restored, or missing from one listing, must not have its history captured again.</summary>
    private void Prune(HashSet<string> live)
    {
        foreach (var id in _windows.Keys.Where(id => !live.Contains(id)).ToList()) _windows.Remove(id);
    }

    private static HubSessionView View(CloudSession s, DateTimeOffset? lastActivity)
    {
        var (repo, branch) = RepoAndBranch(s.Raw);
        return new HubSessionView(s.Id, Title(s), Role(s) ?? "", s.SessionStatus ?? "unknown", repo, branch,
            CreatedAt(s.Raw), lastActivity, s.Tags.ToList());
    }

    private static string Title(CloudSession s) => string.IsNullOrEmpty(s.Title) ? s.Id : s.Title;

    private static string? Role(CloudSession s) =>
        s.Tags.FirstOrDefault(t => t.StartsWith(RoleTagPrefix, StringComparison.Ordinal))?[RoleTagPrefix.Length..];

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

using System.Text.Json;
using Fyrd.Core.CloudSessions;

namespace Fyrd.Core.Hub;

/// <summary>
/// A session that finished its turn waiting on the owner without an <c>AskUserQuestion</c>: the reply ends in a plain-text
/// question. <c>Headline</c> is the cloud's one-line <c>needs_action</c>; <c>Text</c> is the turn's last assistant reply.
/// <c>Id</c> is <c>{sessionId}:{turn}</c>, where the turn is the uuid of its <c>result</c> event, so each turn notifies once.
/// </summary>
public sealed record HubNotification(
    string Id, string SessionId, string SessionTitle, string Headline, string? Text, DateTimeOffset At);

/// <summary>
/// Finds the notification a session's last turn raises. The signal is the one the Claude desktop app pushes on: the post-turn
/// summary's <c>status_category</c> is <c>need_input</c> with a <c>needs_action</c>, while the session is idle.
/// <c>is_noteworthy</c> was false on a pushed turn, so it is not used.
/// </summary>
public static class HubNotifications
{
    public const string NeedInput = "need_input";
    private const int TextMax = 4000;

    public static HubNotification? Find(CloudSession s, string title, IReadOnlyList<SessionEvent> recent)
    {
        if (s.SessionStatus != "idle" || s.Summary is not { StatusCategory: NeedInput, NeedsAction: { } needs }) return null;
        needs = needs.Trim();
        if (needs.Length == 0 || needs.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;

        // The turn is the last result; without one in the window, the newest event stands in for it.
        var end = -1;
        for (var i = recent.Count - 1; i >= 0; i--)
            if (recent[i].Type == "result") { end = i; break; }
        var turn = end >= 0 ? recent[end] : recent.Count > 0 ? recent[^1] : null;
        if (turn is null || string.IsNullOrEmpty(turn.Uuid)) return null;

        // The result carries the turn's final reply; older exports without it fall back to the last main-agent text.
        var text = end >= 0 && Str(recent[end].Raw, "result") is { Length: > 0 } result ? result.Trim() : null;
        for (var i = (end >= 0 ? end : recent.Count) - 1; i >= 0 && text is null; i--)
        {
            var e = recent[i];
            if (e.Type == "assistant" && !IsSubagent(e) && !string.IsNullOrWhiteSpace(e.Text)) text = e.Text.Trim();
        }
        if (text is { Length: > TextMax }) text = text[..TextMax] + "…";

        return new HubNotification($"{s.Id}:{turn.Uuid}", s.Id, title, needs, text, turn.CreatedAt);
    }

    /// <summary>A subagent's or worker's message inside the session (it carries the tool call it answers), not the session's own reply.</summary>
    internal static bool IsSubagent(SessionEvent e) => Str(e.Raw, "parent_tool_use_id") is not null;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}

/// <summary>Notification ids the owner dismissed, in <c>hub/dismissed-notifications.json</c>; the newest <see cref="Keep"/> are kept.</summary>
public sealed class NotificationDismissals(FyrdPaths paths)
{
    public const int Keep = 1000;
    private readonly object _gate = new();
    private readonly string _file = Path.Combine(paths.HubDir, "dismissed-notifications.json");
    private List<string>? _ids;

    public bool Contains(string id)
    {
        lock (_gate) return Load().Contains(id);
    }

    public void Dismiss(string id)
    {
        lock (_gate)
        {
            var ids = Load();
            if (ids.Contains(id)) return;
            ids.Add(id);
            if (ids.Count > Keep) ids.RemoveRange(0, ids.Count - Keep);
            AtomicFile.Write(_file, JsonSerializer.Serialize(ids, Json.Options));
        }
    }

    private List<string> Load()
    {
        if (_ids is not null) return _ids;
        try
        {
            _ids = File.Exists(_file) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_file), Json.Options) ?? [] : [];
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            Console.Error.WriteLine($"fyrd: ignoring unreadable '{_file}': {e.Message}");
            _ids = [];
        }
        return _ids;
    }
}

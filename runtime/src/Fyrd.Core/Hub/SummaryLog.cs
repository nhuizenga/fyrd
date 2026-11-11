using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fyrd.Core.Hub;

/// <summary>
/// Diagnostic log of each session's post-turn summary, to learn which fields mark a turn worth notifying the owner about
/// (the Claude desktop app pushes a notification for some turns that ask nothing). Appends one JSON line per change to
/// <c>hub/post-turn-summaries.jsonl</c> from two sources: <c>poll</c> (the session's top-level <c>post_turn_summary</c>,
/// which carries <c>is_noteworthy</c>, <c>title</c> and <c>description</c>) and <c>stream</c> (the live-only
/// <c>system/post_turn_summary</c> event, which is never saved upstream). Stops writing past <see cref="MaxBytes"/>.
/// </summary>
public sealed class SummaryLog(FyrdPaths paths, TimeProvider? time = null)
{
    public const long MaxBytes = 20 * 1024 * 1024;

    // Shared by every instance: the poll loop and the stream watcher write the same file.
    private static readonly object Gate = new();
    private readonly ConcurrentDictionary<string, string> _lastPolled = new(StringComparer.Ordinal);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string FilePath { get; } = Path.Combine(paths.HubDir, "post-turn-summaries.jsonl");

    /// <summary>Logs the polled session's top-level <c>post_turn_summary</c> when it differs from the last one logged for it.</summary>
    public void RecordPolled(string sessionId, string? title, JsonElement session)
    {
        if (session.ValueKind != JsonValueKind.Object
            || !session.TryGetProperty("post_turn_summary", out var summary) || summary.ValueKind != JsonValueKind.Object)
            return;
        var text = summary.GetRawText();
        if (_lastPolled.TryGetValue(sessionId, out var prev) && prev == text) return;
        _lastPolled[sessionId] = text;
        var status = session.TryGetProperty("session_status", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;
        Append("poll", sessionId, title, status, summary);
    }

    /// <summary>Logs a live <c>system/post_turn_summary</c> event as received.</summary>
    public void RecordStreamed(string sessionId, DateTimeOffset eventAt, JsonElement raw) =>
        Append("stream", sessionId, null, null, raw, eventAt);

    private void Append(string source, string sessionId, string? title, string? status, JsonElement summary,
        DateTimeOffset? eventAt = null)
    {
        var line = new JsonObject
        {
            ["at"] = _time.GetUtcNow().ToString("O"),
            ["source"] = source,
            ["sessionId"] = sessionId,
        };
        if (eventAt is { } ea) line["eventAt"] = ea.ToString("O");
        if (title is not null) line["title"] = title;
        if (status is not null) line["sessionStatus"] = status;
        line["summary"] = JsonNode.Parse(summary.GetRawText());
        try
        {
            lock (Gate)
            {
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes) return;
                File.AppendAllText(FilePath, line.ToJsonString() + "\n");
            }
        }
        catch (IOException e)
        {
            // Diagnostics only: never let the log break a poll or a stream.
            Console.Error.WriteLine($"fyrd: could not write post-turn summary log: {e.Message}");
        }
    }
}

using System.Text;
using System.Text.Json;
using Fyrd.Core.CloudSessions;

namespace Fyrd.Core.Hub;

/// <summary>
/// Folds a session's live-only (ephemeral) events into the one "in progress" line the console shows: the reply text streamed so
/// far (<c>text_delta</c>s) and a status (the current tool, "Thinking…", or the runtime's own <c>task_summary</c>). One per open
/// stream; not thread-safe. A saved transcript event or the end-of-turn summary clears it.
/// </summary>
public sealed class ProgressTracker
{
    public const string Key = "ephemeral";

    private readonly StringBuilder _text = new();
    private string? _task, _block;
    private bool _active;

    /// <summary>Applies one ephemeral event; the new progress item, or null when it changes nothing worth showing.</summary>
    public TranscriptItem? Apply(StreamedEvent e)
    {
        var raw = e.Event.Raw;
        if (raw.ValueKind != JsonValueKind.Object) return null;
        switch (Str(raw, "type"))
        {
            case "system":
                switch (Str(raw, "subtype"))
                {
                    case "task_summary":
                        _task = Str(raw, "detail") is { Length: > 0 } d ? d : null;
                        break;
                    case "post_turn_summary":
                        return Reset(e.Event.CreatedAt);
                    default:
                        return null;
                }
                break;

            case "tool_progress":
                // A long-running tool's heartbeat: keeps the status current instead of a stale "Using Bash".
                if (Str(raw, "tool_name") is not { } tool) return null;
                var secs = raw.TryGetProperty("elapsed_time_seconds", out var el) && el.TryGetDouble(out var n) ? (int)n : 0;
                _block = secs > 0 ? $"Running {tool} · {secs}s" : $"Running {tool}";
                break;

            case "stream_event":
                if (!raw.TryGetProperty("event", out var ev) || ev.ValueKind != JsonValueKind.Object) return null;
                switch (Str(ev, "type"))
                {
                    case "message_start":
                        _text.Clear();
                        _block = null;
                        break;
                    case "content_block_start":
                        _block = ev.TryGetProperty("content_block", out var cb) ? Str(cb, "type") switch
                        {
                            "tool_use" => "Using " + (Str(cb, "name") ?? "a tool"),
                            "thinking" => "Thinking…",
                            _ => null,
                        } : null;
                        if (_block is null) return null;
                        break;
                    case "content_block_delta":
                        if (!ev.TryGetProperty("delta", out var delta) || Str(delta, "type") != "text_delta"
                            || Str(delta, "text") is not { Length: > 0 } text)
                            return null;
                        _text.Append(text);
                        break;
                    default:
                        return null;
                }
                break;

            default:
                return null;
        }
        _active = true;
        return Item(e.Event.CreatedAt);
    }

    /// <summary>Clears the line (a saved event arrived, or the turn ended); the "nothing in progress" item once, or null if already clear.</summary>
    public TranscriptItem? Reset(DateTimeOffset at)
    {
        _text.Clear();
        _task = _block = null;
        if (!_active) return null;
        _active = false;
        return Item(at);
    }

    private TranscriptItem Item(DateTimeOffset at) =>
        new(Key, 0, "agent", "progress", _active ? _text.ToString() : "", at, Status: _active ? _task ?? _block : null);

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fyrd.Core.CloudSessions;

namespace Fyrd.Core.Hub;

/// <summary>
/// Folds a session's live-only (ephemeral) events into the one "in progress" line the console shows: the reply text streamed so
/// far (<c>text_delta</c>s) and a status (the current tool, "Thinking…", or the runtime's own <c>task_summary</c>). One per open
/// stream; not thread-safe. A saved transcript event or the end-of-turn summary clears it.
/// </summary>
public sealed partial class ProgressTracker
{
    private const int DetailMax = 600;
    // Tool inputs can be hundreds of KB (a Write's content): only the head is scanned, and only until the detail is settled.
    private const int JsonScanMax = 8192;

    // The argument worth showing for a tool, most telling first; matched in the partial JSON as it streams in.
    private static readonly string[] DetailKeys = ["command", "pattern", "file_path", "notebook_path", "path", "url", "query", "description", "prompt"];

    [GeneratedRegex(@"""(?<k>[a-z_]+)""\s*:\s*""(?<v>(?:[^""\\]|\\.)*)(?<end>"")?")]
    private static partial Regex StringField();

    public const string Key = "ephemeral";

    private readonly StringBuilder _text = new();
    private readonly StringBuilder _json = new();
    private string? _task, _block, _detail;
    private bool _settled;
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
                        _block = _detail = null;
                        _json.Clear();
                        _settled = false;
                        break;
                    case "content_block_start":
                        _json.Clear();
                        _detail = null;
                        _settled = false;
                        _block = ev.TryGetProperty("content_block", out var cb) ? Str(cb, "type") switch
                        {
                            "tool_use" => "Using " + (Str(cb, "name") ?? "a tool"),
                            "thinking" => "Thinking…",
                            _ => null,
                        } : null;
                        if (_block is null) return null;
                        break;
                    case "content_block_delta":
                        if (!ev.TryGetProperty("delta", out var delta)) return null;
                        if (Str(delta, "type") == "input_json_delta")
                        {
                            // The tool's arguments stream as partial JSON; show the telling one (the command, the path…) as it grows.
                            if (_settled || Str(delta, "partial_json") is not { Length: > 0 } part) return null;
                            _json.Append(part);
                            var (found, closed) = ExtractDetail(_json.ToString());
                            // Once the value is complete (or too long to show), or the scanned head is full, later deltas are ignored.
                            if (closed || _json.Length >= JsonScanMax || found is { Length: >= DetailMax }) _settled = true;
                            if (found is null || found == _detail) return null;
                            _detail = found;
                            break;
                        }
                        if (Str(delta, "type") != "text_delta" || Str(delta, "text") is not { Length: > 0 } text)
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
        _task = _block = _detail = null;
        _json.Clear();
        _settled = false;
        if (!_active) return null;
        _active = false;
        return Item(at);
    }

    private TranscriptItem Item(DateTimeOffset at) =>
        new(Key, 0, "agent", "progress", _active ? _text.ToString() : "", at, Status: _active ? _task ?? _block : null,
            Detail: _active ? _detail : null);

    /// <summary>The first telling string argument in partial tool-input JSON (a half-written value counts), and whether its string has closed.</summary>
    internal static (string? Text, bool Closed) ExtractDetail(string partialJson)
    {
        var fields = new Dictionary<string, (string Raw, bool Closed)>();
        foreach (Match m in StringField().Matches(partialJson)) fields.TryAdd(m.Groups["k"].Value, (m.Groups["v"].Value, m.Groups["end"].Success));
        foreach (var key in DetailKeys)
        {
            if (!fields.TryGetValue(key, out var f) || f.Raw.Length == 0) continue;
            var text = Unescape(f.Raw);
            return (text.Length > DetailMax ? text[..DetailMax] + "…" : text, f.Closed);
        }
        return (null, false);
    }

    // A value cut off mid-escape (a trailing lone backslash, half a \uXXXX) does not parse: drop the tail and retry.
    private static string Unescape(string raw)
    {
        for (var s = raw; s.Length > 0; s = s[..^1])
        {
            try { return JsonSerializer.Deserialize<string>("\"" + s + "\"") ?? ""; }
            catch (JsonException) { }
        }
        return "";
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

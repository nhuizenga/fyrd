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
    private bool _active, _dirty;
    // Running forked workers by the Agent call's tool_use id, in start order; Line is each one's latest tool.
    private readonly List<(string Id, string Description, string? Line)> _workers = [];

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
                var line = secs > 0 ? $"Running {tool} · {secs}s" : $"Running {tool}";
                // A heartbeat whose parent is a running worker is that worker's tool; any other parent is the main agent's own call.
                if (WorkerIndex(Str(raw, "parent_tool_use_id")) is { } wi) return SetWorkerLine(wi, line, e.Event.CreatedAt);
                _block = line;
                break;

            case "stream_event":
                if (!raw.TryGetProperty("event", out var ev) || ev.ValueKind != JsonValueKind.Object) return null;
                if (Str(raw, "parent_tool_use_id") is { } streamParent)
                {
                    // A worker's own stream: only the start of one of its tool calls is shown, as that worker's latest line.
                    if (WorkerIndex(streamParent) is not { } wj || Str(ev, "type") != "content_block_start"
                        || !ev.TryGetProperty("content_block", out var wcb) || Str(wcb, "type") != "tool_use") return null;
                    return SetWorkerLine(wj, "Using " + (Str(wcb, "name") ?? "a tool"), e.Event.CreatedAt);
                }
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
                            "tool_use" => Str(cb, "name") is "Agent" ? "Starting a worker" : "Using " + (Str(cb, "name") ?? "a tool"),
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

    /// <summary>
    /// Notes a saved event: a forked worker starting (<c>task_started</c>, <c>task_type local_agent</c>) or ending (<c>task_notification</c>
    /// for it) changes the running-workers list. Call before <see cref="Reset"/> for the same event, which reports the change.
    /// </summary>
    public void ApplySaved(StreamedEvent e)
    {
        var raw = e.Event.Raw;
        if (Str(raw, "type") != "system" || Str(raw, "tool_use_id") is not { } id) return;
        switch (Str(raw, "subtype"))
        {
            case "task_started" when Str(raw, "task_type") == "local_agent" && WorkerIndex(id) is null:
                _workers.Add((id, Str(raw, "description") ?? "Worker", null));
                _dirty = true;
                break;
            case "task_notification" when WorkerIndex(id) is { } i:
                _workers.RemoveAt(i);
                _dirty = true;
                break;
        }
    }

    /// <summary>Clears the line (a saved event arrived, or the turn ended); the new item if the line or the workers list changed, or null if nothing did.</summary>
    public TranscriptItem? Reset(DateTimeOffset at)
    {
        _text.Clear();
        _task = _block = _detail = null;
        _json.Clear();
        _settled = false;
        if (!_active && !_dirty) return null;
        _active = false;
        _dirty = false;
        return Item(at);
    }

    private int? WorkerIndex(string? toolUseId) =>
        toolUseId is null ? null : _workers.FindIndex(w => w.Id == toolUseId) is var i and >= 0 ? i : null;

    private TranscriptItem? SetWorkerLine(int index, string line, DateTimeOffset at)
    {
        if (_workers[index].Line == line) return null;
        _workers[index] = _workers[index] with { Line = line };
        return Item(at);
    }

    // Running workers keep the item alive after the main agent's own line clears: "Waiting for workers" is its status then.
    private TranscriptItem Item(DateTimeOffset at) =>
        new(Key, 0, "agent", "progress", _active ? _text.ToString() : "", at,
            Status: _active ? _task ?? _block : _workers.Count > 0 ? "Waiting for workers" : null,
            Detail: _active ? _detail : null,
            Workers: _workers.Count > 0 ? _workers.Select(w => new TranscriptWorker(w.Id, w.Description, w.Line)).ToList() : null);

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

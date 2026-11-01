using System.Text;
using System.Text.Json;
using Fyrd.Core.CloudSessions;

namespace Fyrd.Core.Hub;

public sealed record TranscriptOption(string Label, string? Description);

public sealed record TranscriptQuestion(string Question, string? Header, bool MultiSelect, IReadOnlyList<TranscriptOption> Options);

/// <summary>
/// One line of a session's transcript as the console shows it. <c>Role</c> is <c>agent</c> or <c>user</c>; <c>Kind</c> is
/// <c>text</c>, <c>question</c> (an <c>AskUserQuestion</c> call) or <c>answer</c> (its reply). <c>Key</c> is
/// <c>{Seq}:{index}</c>, unique per item.
/// </summary>
public sealed record TranscriptItem(
    string Key,
    long Seq,
    string Role,
    string Kind,
    string Text,
    DateTimeOffset At,
    IReadOnlyList<TranscriptQuestion>? Questions = null,
    string? ToolUseId = null);

/// <summary>Turns a streamed cloud-session event into the transcript items the console shows; pure, never throws on bad payloads.</summary>
public static class HubTranscript
{
    private const string AskTool = "AskUserQuestion";

    public static IReadOnlyList<TranscriptItem> Map(StreamedEvent e)
    {
        var items = new List<TranscriptItem>();
        var raw = e.Event.Raw;
        if (raw.ValueKind != JsonValueKind.Object) return items;

        void Add(string role, string kind, string text, IReadOnlyList<TranscriptQuestion>? questions = null, string? toolUseId = null) =>
            items.Add(new TranscriptItem($"{e.Sequence}:{items.Count}", e.Sequence, role, kind, text, e.Event.CreatedAt, questions, toolUseId));

        switch (Str(raw, "type"))
        {
            case "assistant":
                if (Content(raw) is not { ValueKind: JsonValueKind.Array } blocks) break;
                var text = JoinText(blocks);
                if (!string.IsNullOrWhiteSpace(text)) Add("agent", "text", text);
                foreach (var b in blocks.EnumerateArray())
                {
                    if (Str(b, "type") != "tool_use" || Str(b, "name") != AskTool) continue;
                    var questions = Questions(Obj(b, "input"));
                    if (questions.Count == 0) continue;
                    Add("agent", "question", string.Join("\n", questions.Select(q => q.Question)), questions, Str(b, "id"));
                }
                break;

            case "user":
                // A user message with a parent tool use is a subagent's prompt (Task tool), not the owner speaking.
                if (Str(raw, "parent_tool_use_id") is not null) break;
                var userText = Content(raw) switch
                {
                    { ValueKind: JsonValueKind.String } s => s.GetString(),
                    { ValueKind: JsonValueKind.Array } a => JoinText(a),
                    _ => null,
                };
                if (!string.IsNullOrWhiteSpace(userText)) Add("user", "text", userText);
                break;

            case "control_response":
                if (Obj(raw, "response") is not { } outer || Obj(outer, "response") is not { } inner
                    || Str(inner, "tool_name") != AskTool
                    || Obj(inner, "updatedInput") is not { } input
                    || Obj(input, "answers") is not { } answers)
                    break;
                var lines = answers.EnumerateObject()
                    .Select(p => $"{p.Name} → {(p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText())}")
                    .ToList();
                if (lines.Count > 0) Add("user", "answer", string.Join("\n", lines), toolUseId: Str(inner, "toolUseID"));
                break;
        }
        return items;
    }

    private static List<TranscriptQuestion> Questions(JsonElement? input)
    {
        var result = new List<TranscriptQuestion>();
        if (input is not { } i || !i.TryGetProperty("questions", out var qs) || qs.ValueKind != JsonValueKind.Array) return result;
        foreach (var q in qs.EnumerateArray())
        {
            if (Str(q, "question") is not { } question) continue;
            var options = q.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array
                ? opts.EnumerateArray()
                    .Where(o => Str(o, "label") is not null)
                    .Select(o => new TranscriptOption(Str(o, "label")!, Str(o, "description")))
                    .ToList()
                : [];
            var multi = q.TryGetProperty("multiSelect", out var m) && m.ValueKind == JsonValueKind.True;
            result.Add(new TranscriptQuestion(question, Str(q, "header"), multi, options));
        }
        return result;
    }

    private static JsonElement? Content(JsonElement raw) =>
        Obj(raw, "message") is { } msg && msg.TryGetProperty("content", out var c) ? c : null;

    private static string JoinText(JsonElement blocks)
    {
        var sb = new StringBuilder();
        foreach (var b in blocks.EnumerateArray())
            if (Str(b, "type") == "text" && Str(b, "text") is { } t) sb.Append(t);
        return sb.ToString();
    }

    private static JsonElement? Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object
            ? v : null;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}

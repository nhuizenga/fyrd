using System.Text;
using System.Text.Json;
using Fyrd.Core.CloudSessions;

namespace Fyrd.Core.Hub;

public sealed record TranscriptOption(string Label, string? Description);

public sealed record TranscriptQuestion(string Question, string? Header, bool MultiSelect, IReadOnlyList<TranscriptOption> Options);

/// <summary>
/// One line of a session's transcript as the console shows it. <c>Role</c> is <c>agent</c> or <c>user</c>; <c>Kind</c> is
/// <c>text</c>, <c>question</c> (an <c>AskUserQuestion</c> call), <c>answer</c> (its reply) or <c>worker</c> (an <c>Agent</c> tool call that started a worker: <c>Text</c> is its description, <c>Detail</c> its subagent type), <c>worker-result</c> (that worker's returned result as <c>Text</c>, linked by <c>ToolUseId</c>) or <c>progress</c> (live-only: <c>Text</c> is the reply streamed so far, <c>Status</c> what the agent is doing, <c>Detail</c> the tool's main argument such as the Bash command; both empty means nothing is in progress). <c>Key</c> is
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
    string? ToolUseId = null,
    string? Status = null,
    string? Detail = null);

/// <summary>Turns a streamed cloud-session event into the transcript items the console shows; pure, never throws on bad payloads.</summary>
public static class HubTranscript
{
    private const string AskTool = "AskUserQuestion";
    private const string AgentTool = "Agent";

    /// <summary>
    /// Maps one event. <paramref name="workerToolIds"/> remembers the <c>Agent</c> tool calls seen on this stream, so a later
    /// <c>tool_result</c> for one of them becomes a <c>worker-result</c> item; pass the same set for the whole stream.
    /// </summary>
    public static IReadOnlyList<TranscriptItem> Map(StreamedEvent e, ISet<string>? workerToolIds = null)
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
                // Subagent output (parent_tool_use_id) is never the peasant's own: only its own Agent calls start a worker item.
                if (Str(raw, "parent_tool_use_id") is null)
                    foreach (var b in blocks.EnumerateArray())
                    {
                        if (Str(b, "type") != "tool_use" || Str(b, "name") != AgentTool) continue;
                        var agentInput = Obj(b, "input");
                        var description = agentInput is { } i ? Str(i, "description") ?? Str(i, "prompt") : null;
                        var id = Str(b, "id");
                        if (id is not null) workerToolIds?.Add(id);
                        items.Add(new TranscriptItem($"{e.Sequence}:{items.Count}", e.Sequence, "agent", "worker",
                            description ?? "Worker", e.Event.CreatedAt, ToolUseId: id,
                            Detail: agentInput is { } j ? Str(j, "subagent_type") : null));
                    }
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
                if (workerToolIds is { Count: > 0 } && Content(raw) is { ValueKind: JsonValueKind.Array } results)
                    foreach (var b in results.EnumerateArray())
                    {
                        if (Str(b, "type") != "tool_result" || Str(b, "tool_use_id") is not { } toolId || !workerToolIds.Contains(toolId)) continue;
                        var result = b.TryGetProperty("content", out var c)
                            ? c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : c.ValueKind == JsonValueKind.Array ? JoinText(c) : ""
                            : "";
                        Add("agent", "worker-result", result, toolUseId: toolId);
                    }
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

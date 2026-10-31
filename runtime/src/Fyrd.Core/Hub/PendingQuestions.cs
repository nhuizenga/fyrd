using System.Text.Json;
using Fyrd.Core.CloudSessions;

namespace Fyrd.Core.Hub;

/// <summary>
/// Finds unanswered <c>AskUserQuestion</c> items: a <c>control_request</c> with subtype <c>can_use_tool</c> and tool
/// <c>AskUserQuestion</c> is pending while no <c>control_response</c> carries its <c>request_id</c>.
/// </summary>
public static class PendingQuestions
{
    public static IReadOnlyList<HubQuestion> Find(string sessionId, string title, IReadOnlyList<SessionEvent> events)
    {
        var answered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in events)
        {
            if (e.Type == "control_response" && Obj(e.Raw, "response") is { } resp && Str(resp, "request_id") is { } rid)
                answered.Add(rid);
        }

        var result = new List<HubQuestion>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in events)
        {
            if (e.Type != "control_request") continue;
            if (Str(e.Raw, "request_id") is not { } requestId || answered.Contains(requestId) || !seen.Add(requestId)) continue;
            if (Obj(e.Raw, "request") is not { } req
                || Str(req, "subtype") != "can_use_tool"
                || Str(req, "tool_name") != "AskUserQuestion"
                || Obj(req, "input") is not { } input
                || !input.TryGetProperty("questions", out var questions)
                || questions.ValueKind != JsonValueKind.Array)
                continue;

            var toolUseId = Str(req, "tool_use_id") ?? "";
            var inputCopy = input.Clone();
            var index = 0;
            foreach (var q in questions.EnumerateArray())
            {
                var i = index++;
                if (Str(q, "question") is not { } text) continue;
                var options = q.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array
                    ? opts.EnumerateArray().Select(o => Str(o, "label")).OfType<string>().ToList()
                    : [];
                var multi = q.TryGetProperty("multiSelect", out var m) && m.ValueKind == JsonValueKind.True;
                result.Add(new HubQuestion($"{sessionId}:{requestId}:{i}", sessionId, title, text, options, multi,
                    e.CreatedAt, toolUseId, requestId, inputCopy));
            }
        }
        return result;
    }

    private static JsonElement? Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object
            ? v : null;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}

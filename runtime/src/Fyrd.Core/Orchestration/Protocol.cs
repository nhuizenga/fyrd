using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fyrd.Core.Orchestration;

public sealed record UnitDecl(string Id, string Title, bool Done);

public sealed record QuestionDecl(
    string Id, string Question, IReadOnlyList<string> Options,
    string? Recommendation, string? Reason, string? Unit, string? Details);

public sealed record DoneDecl(string Branch, string? Summary);

public sealed record FailedDecl(string Reason);

public sealed record ParsedBlocks(
    IReadOnlyList<UnitDecl>? Units,
    IReadOnlyList<QuestionDecl> Questions,
    DoneDecl? Done,
    FailedDecl? Failed,
    IReadOnlyList<string> Warnings);

/// <summary>Parses and formats the fenced fyrd-* blocks exchanged with the lead agent.</summary>
public static partial class Protocol
{
    [GeneratedRegex(
        @"^[ \t]*(?<fence>`{3,})(?<name>fyrd-[a-z]+)[ \t]*\r?\n(?<body>.*?)\r?\n[ \t]*\k<fence>[ \t]*\r?$",
        RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex FenceRegex();

    [GeneratedRegex("^[a-z0-9-]{1,40}$", RegexOptions.CultureInvariant)]
    private static partial Regex UnitIdRegex();

    public static ParsedBlocks Parse(string assistantText)
    {
        var warnings = new List<string>();
        var questions = new List<QuestionDecl>();
        List<UnitDecl>? units = null;
        DoneDecl? done = null;
        FailedDecl? failed = null;

        foreach (Match m in FenceRegex().Matches(assistantText ?? ""))
        {
            var name = m.Groups["name"].Value;
            if (name is not ("fyrd-units" or "fyrd-question" or "fyrd-done" or "fyrd-failed")) continue;
            try
            {
                using var doc = JsonDocument.Parse(m.Groups["body"].Value);
                var root = doc.RootElement;
                switch (name)
                {
                    case "fyrd-units":
                        var parsed = ParseUnits(root, warnings);
                        if (parsed is not null) units = parsed;
                        break;
                    case "fyrd-question":
                        var q = ParseQuestion(root, warnings);
                        if (q is not null) questions.Add(q);
                        break;
                    case "fyrd-done":
                        var d = ParseDone(root, warnings);
                        if (d is not null) done = d;
                        break;
                    case "fyrd-failed":
                        var f = ParseFailed(root, warnings);
                        if (f is not null) failed = f;
                        break;
                }
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
            {
                warnings.Add($"{name}: malformed block ignored ({e.Message})");
            }
        }

        return new ParsedBlocks(units, questions, done, failed, warnings);
    }

    public static string FormatAnswer(string questionId, string answer) =>
        Fence("fyrd-answer", JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["id"] = questionId,
            ["answer"] = answer,
        }));

    public static string FormatConfig(int squadCap) =>
        Fence("fyrd-config", "{\"squadCap\":" + squadCap.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}");

    private static string Fence(string name, string json) => "```" + name + "\n" + json + "\n```";

    private static List<UnitDecl>? ParseUnits(JsonElement root, List<string> warnings)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            warnings.Add("fyrd-units: body must be a JSON array; block ignored");
            return null;
        }
        var list = new List<UnitDecl>();
        foreach (var el in root.EnumerateArray())
        {
            var id = Str(el, "id");
            var title = Str(el, "title");
            if (id is null || !UnitIdRegex().IsMatch(id))
            {
                warnings.Add($"fyrd-units: dropped unit with invalid id '{id}'");
                continue;
            }
            var done = el.ValueKind == JsonValueKind.Object
                && el.TryGetProperty("done", out var d) && d.ValueKind == JsonValueKind.True;
            list.Add(new UnitDecl(id, title ?? id, done));
        }
        return list;
    }

    private static QuestionDecl? ParseQuestion(JsonElement el, List<string> warnings)
    {
        var id = Str(el, "id");
        var question = Str(el, "question");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(question))
        {
            warnings.Add("fyrd-question: dropped question missing id or question");
            return null;
        }
        var options = new List<string>();
        if (el.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Array)
            foreach (var item in o.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String) options.Add(item.GetString()!);
        return new QuestionDecl(id, question, options,
            Str(el, "recommendation"), Str(el, "reason"), Str(el, "unit"), Str(el, "details"));
    }

    private static DoneDecl? ParseDone(JsonElement el, List<string> warnings)
    {
        var branch = Str(el, "branch");
        if (string.IsNullOrWhiteSpace(branch))
        {
            warnings.Add("fyrd-done: missing branch; block ignored");
            return null;
        }
        return new DoneDecl(branch, Str(el, "summary"));
    }

    private static FailedDecl? ParseFailed(JsonElement el, List<string> warnings)
    {
        var reason = Str(el, "reason");
        if (string.IsNullOrWhiteSpace(reason))
        {
            warnings.Add("fyrd-failed: missing reason; block ignored");
            return null;
        }
        return new FailedDecl(reason);
    }

    private static string? Str(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}

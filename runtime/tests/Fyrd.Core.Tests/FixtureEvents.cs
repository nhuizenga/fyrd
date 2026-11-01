using System.Text.Json;
using Fyrd.Core.CloudSessions;

namespace Fyrd.Core.Tests;

/// <summary>Loads raw-event JSON fixtures (copied to the test output) as <see cref="SessionEvent"/>s, parsed like the client does.</summary>
public static class FixtureEvents
{
    public static IReadOnlyList<SessionEvent> Load(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.EnumerateArray().Select(FromRaw).ToList();
    }

    public static SessionEvent FromRaw(JsonElement e)
    {
        var type = Str(e, "type") ?? "";
        string? text = null;
        if (type is "user" or "assistant"
            && e.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object
            && msg.TryGetProperty("content", out var content))
        {
            if (content.ValueKind == JsonValueKind.String) text = content.GetString();
            else if (content.ValueKind == JsonValueKind.Array)
                text = string.Concat(content.EnumerateArray().Where(b => Str(b, "type") == "text").Select(b => Str(b, "text")));
        }
        var created = DateTimeOffset.TryParse(Str(e, "created_at"), out var dt) ? dt : default;
        return new SessionEvent(Str(e, "uuid") ?? "", type, created, text, e.Clone());
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}

using System.Text.Json;
using Fyrd.Core.CloudSessions;
using Fyrd.Core.Orchestration;
using Fyrd.Core.Specs;

namespace Fyrd.Server.Endpoints;

internal sealed record LeadView(string SessionId, string Url, string? Status, long? ContextUsed, long? ContextMax);

internal sealed record PeerView(string SessionId, string Url, string Role, string? UnitId, string? Status);

internal sealed record RateLimitView(string? Status, string? ResetsAt);

internal sealed record BoardSpecView(SpecListEntry Entry, IReadOnlyList<Unit> Units, LeadView? Lead, IReadOnlyList<PeerView> Peers);

internal sealed record BoardView(IReadOnlyList<BoardSpecView> Specs, bool SignedOut, string? LastError, DateTimeOffset? LastPollAt, RateLimitView? RateLimit);

internal static class BoardEndpoints
{
    public static IEndpointRouteBuilder MapBoard(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/board", (SpecStore specs, RunnerState state) =>
        {
            var sessions = state.Sessions.Values.ToList();
            var views = new List<BoardSpecView>();
            foreach (var entry in specs.List())
            {
                var units = Safe(() => specs.ListUnits(entry.Id));
                LeadView? lead = null;
                var peers = new List<PeerView>();
                if (entry.Run is not null)
                {
                    if (entry.Run.SessionId is { } sid && state.Sessions.TryGetValue(sid, out var s))
                    {
                        var (used, max) = Context(s.Raw);
                        lead = new LeadView(sid, Url(sid), s.SessionStatus, used, max);
                    }
                    else if (entry.Run.SessionId is { } sid2)
                    {
                        lead = new LeadView(sid2, Url(sid2), null, null, null);
                    }
                    var specTag = "fyrd-spec-" + entry.Id;
                    foreach (var p in sessions.Where(p => p.Tags.Contains(specTag) && p.Id != entry.Run.SessionId && p.SessionStatus != "archived"))
                    {
                        var role = TagValue(p, "fyrd-role-");
                        if (role is null || role == "lead") continue;
                        peers.Add(new PeerView(p.Id, Url(p.Id), role, TagValue(p, "fyrd-unit-"), p.SessionStatus));
                    }
                }
                views.Add(new BoardSpecView(entry, units, lead, peers));
            }
            return Results.Ok(new BoardView(views, state.SignedOut, state.LastError, state.LastPollAt, RateLimit(sessions)));
        });
        return app;
    }

    private static string Url(string sessionId) => $"https://claude.ai/code/{sessionId}";

    private static IReadOnlyList<Unit> Safe(Func<IReadOnlyList<Unit>> f)
    {
        try { return f(); }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or ArgumentException) { return []; }
    }

    private static string? TagValue(CloudSession s, string prefix) =>
        s.Tags.FirstOrDefault(t => t.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];

    private static JsonElement? Meta(JsonElement raw, string name) =>
        raw.ValueKind == JsonValueKind.Object
        && raw.TryGetProperty("external_metadata", out var m) && m.ValueKind == JsonValueKind.Object
        && m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static (long?, long?) Context(JsonElement raw)
    {
        if (Meta(raw, "context_usage") is not { } u) return (null, null);
        return (Long(u, "used_tokens"), Long(u, "max_tokens"));
    }

    private static long? Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;

    // The wire value is epoch seconds (a number); strings are passed through untouched.
    private static string? ResetsAt(JsonElement r)
    {
        if (r.TryGetProperty("resetsAt", out var v) && v.ValueKind == JsonValueKind.Number)
        {
            if (!v.TryGetInt64(out var secs)) return null;
            try { return DateTimeOffset.FromUnixTimeSeconds(secs).ToString("O"); }
            catch (ArgumentOutOfRangeException) { return null; }
        }
        return Str(r, "resetsAt");
    }

    private static RateLimitView? RateLimit(IEnumerable<CloudSession> sessions)
    {
        foreach (var s in sessions)
            if (Meta(s.Raw, "rate_limit_info") is { } r)
                return new RateLimitView(Str(r, "status"), ResetsAt(r));
        return null;
    }
}

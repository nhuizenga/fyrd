using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Fyrd.Core.CloudSessions;

/// <summary>
/// The only code that knows the undocumented Claude Code cloud-session HTTP API.
/// The wire format is snake_case, so JSON is built and parsed explicitly.
/// </summary>
public sealed partial class CloudSessionClient : ICloudSessions
{
    private static readonly Uri BaseAddress = new("https://api.anthropic.com");

    private readonly HttpClient _http;
    private readonly ClaudeCredentials _credentials;

    private const int MaxListPages = 20;

    public CloudSessionClient(HttpClient http, ClaudeCredentials credentials)
    {
        _http = http;
        _credentials = credentials;
    }

    [GeneratedRegex("^session_[A-Za-z0-9]+$")]
    private static partial Regex SessionIdPattern();

    public async Task<CloudSession> CreateAsync(CreateSessionRequest request, CancellationToken ct = default)
    {
        var sources = new JsonArray();
        if (request.Source is { } src)
            sources.Add(new JsonObject
            {
                ["type"] = "git_repository",
                ["url"] = src.RepoUrl,
                ["revision"] = src.Revision,
            });

        var context = new JsonObject
        {
            ["sources"] = sources,
            ["outcomes"] = new JsonArray(),
            ["environment_variables"] = new JsonObject(),
            ["model"] = request.Model,
        };
        if (request.AppendSystemPrompt is not null) context["append_system_prompt"] = request.AppendSystemPrompt;

        var events = new JsonArray();
        if (request.PermissionMode is { } mode)
            events.Add(new JsonObject
            {
                ["type"] = "event",
                ["data"] = new JsonObject
                {
                    ["type"] = "control_request",
                    ["request_id"] = "set-mode-" + Guid.NewGuid().ToString("N"),
                    ["request"] = new JsonObject { ["subtype"] = "set_permission_mode", ["mode"] = mode },
                },
            });
        events.Add(new JsonObject { ["type"] = "event", ["data"] = UserEvent(request.FirstMessage) });

        var body = new JsonObject
        {
            ["title"] = request.Title,
            ["tags"] = new JsonArray(request.Tags.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
            ["environment_id"] = request.EnvironmentId,
            ["events"] = events,
            ["session_context"] = context,
        };

        using var doc = await SendAsync(HttpMethod.Post, "/v1/sessions", body, ct);
        return ParseSession(doc.RootElement);
    }

    public async Task<CloudSession> GetAsync(string sessionId, CancellationToken ct = default)
    {
        ValidateSessionId(sessionId);
        using var doc = await SendAsync(HttpMethod.Get, $"/v1/sessions/{sessionId}", null, ct);
        return ParseSession(doc.RootElement);
    }

    public async Task<EventPage> ListEventsAsync(
        string sessionId, int limit = 100, string? afterId = null, string? beforeId = null, CancellationToken ct = default)
    {
        ValidateSessionId(sessionId);
        var query = new StringBuilder($"limit={limit}");
        if (afterId is not null) query.Append("&after_id=").Append(Uri.EscapeDataString(afterId));
        if (beforeId is not null) query.Append("&before_id=").Append(Uri.EscapeDataString(beforeId));

        using var doc = await SendAsync(HttpMethod.Get, $"/v1/sessions/{sessionId}/events?{query}", null, ct);
        var root = doc.RootElement;
        var events = new List<SessionEvent>();
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var e in data.EnumerateArray()) events.Add(ParseEvent(e));

        return new EventPage(
            events,
            root.TryGetProperty("has_more", out var hm) && hm.ValueKind == JsonValueKind.True,
            Str(root, "first_id"),
            Str(root, "last_id"));
    }

    public async Task SendMessageAsync(string sessionId, string text, CancellationToken ct = default)
    {
        ValidateSessionId(sessionId);
        var body = new JsonObject { ["events"] = new JsonArray(UserEvent(text)) };
        using var _ = await SendAsync(HttpMethod.Post, $"/v1/sessions/{sessionId}/events", body, ct);
    }

    public async Task<IReadOnlyList<CloudSession>> ListByTagAsync(string tag, CancellationToken ct = default)
    {
        var sessions = new List<CloudSession>();
        string? afterId = null;
        for (var page = 0; page < MaxListPages; page++)
        {
            var query = $"tags={Uri.EscapeDataString(tag)}&limit=100";
            if (afterId is not null) query += "&after_id=" + Uri.EscapeDataString(afterId);

            using var doc = await SendAsync(HttpMethod.Get, $"/v1/sessions?{query}", null, ct);
            var root = doc.RootElement;
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                foreach (var s in data.EnumerateArray()) sessions.Add(ParseSession(s));

            afterId = Str(root, "last_id");
            if (!(root.TryGetProperty("has_more", out var hm) && hm.ValueKind == JsonValueKind.True) || afterId is null)
                break;
        }

        return sessions;
    }

    public async Task InterruptAsync(string sessionId, CancellationToken ct = default)
    {
        ValidateSessionId(sessionId);
        var body = new JsonObject
        {
            ["events"] = new JsonArray(new JsonObject
            {
                ["type"] = "control_request",
                ["request_id"] = Guid.NewGuid().ToString(),
                ["request"] = new JsonObject { ["subtype"] = "interrupt", ["cancel_queued"] = true },
            }),
        };
        using var _ = await SendAsync(HttpMethod.Post, $"/v1/sessions/{sessionId}/events", body, ct);
    }

    public async Task ArchiveAsync(string sessionId, CancellationToken ct = default)
    {
        ValidateSessionId(sessionId);
        using var _ = await SendAsync(HttpMethod.Post, $"/v1/code/sessions/{sessionId}/archive", new JsonObject(), ct);
    }

    private static void ValidateSessionId(string sessionId)
    {
        if (sessionId is null || !SessionIdPattern().IsMatch(sessionId))
            throw new ArgumentException("Session id must match ^session_[A-Za-z0-9]+$.", nameof(sessionId));
    }

    private static JsonObject UserEvent(string text) => new()
    {
        ["uuid"] = Guid.NewGuid().ToString(),
        ["session_id"] = "",
        ["type"] = "user",
        ["parent_tool_use_id"] = null,
        ["message"] = new JsonObject { ["role"] = "user", ["content"] = text },
    };

    private async Task<JsonDocument> SendAsync(HttpMethod method, string pathAndQuery, JsonNode? body, CancellationToken ct)
    {
        var auth = _credentials.Read();
        using var req = new HttpRequestMessage(method, new Uri(BaseAddress, pathAndQuery));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        req.Headers.Add("x-organization-uuid", auth.OrganizationUuid);
        req.Headers.Add("anthropic-version", "2023-06-01");
        req.Headers.Add("anthropic-beta", "ccr-byoc-2025-07-29");
        if (body is not null)
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        var status = (int)resp.StatusCode;
        if (status is 401 or 403) throw new CloudSessionAuthException(status);
        if (status is < 200 or >= 300) throw new CloudSessionApiException(status, text);

        try
        {
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        }
        catch (JsonException)
        {
            throw new CloudSessionApiException(status, "<non-JSON body>");
        }
    }

    private static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static CloudSession ParseSession(JsonElement root)
    {
        PostTurnSummary? summary = null;
        if (root.TryGetProperty("external_metadata", out var meta) && meta.ValueKind == JsonValueKind.Object
            && meta.TryGetProperty("post_turn_summary", out var s) && s.ValueKind == JsonValueKind.Object)
            summary = new PostTurnSummary(
                Str(s, "status_category"), Str(s, "needs_action"), Str(s, "recent_action"), Str(s, "status_detail"));

        var tags = new List<string>();
        if (root.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array)
            tags.AddRange(t.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));

        return new CloudSession(
            Str(root, "id") ?? throw new CloudSessionApiException(200, "Response had no session id."),
            Str(root, "title"), Str(root, "session_status"), Str(root, "connection_status"),
            summary, tags, root.Clone());
    }

    private static SessionEvent ParseEvent(JsonElement e)
    {
        var type = Str(e, "type") ?? "";
        string? text = null;
        if (type is "user" or "assistant"
            && e.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object
            && msg.TryGetProperty("content", out var content))
        {
            if (content.ValueKind == JsonValueKind.String)
                text = content.GetString();
            else if (content.ValueKind == JsonValueKind.Array)
                text = string.Concat(content.EnumerateArray()
                    .Where(b => Str(b, "type") == "text")
                    .Select(b => Str(b, "text")));
        }

        var created = DateTimeOffset.TryParse(Str(e, "created_at"), out var dt) ? dt : default;
        return new SessionEvent(Str(e, "uuid") ?? "", type, created, text, e.Clone());
    }
}

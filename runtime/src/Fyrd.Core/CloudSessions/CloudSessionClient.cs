using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
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
        events.Add(new JsonObject { ["type"] = "event", ["data"] = UserEvent(request.FirstMessage, "") });

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
        var body = new JsonObject { ["events"] = new JsonArray(UserEvent(text, sessionId)) };
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

    public async Task RenameAsync(string sessionId, string title, CancellationToken ct = default)
    {
        ValidateSessionId(sessionId);
        using var _ = await SendAsync(HttpMethod.Patch, $"/v1/sessions/{sessionId}", new JsonObject { ["title"] = title }, ct);
    }

    public async Task AnswerQuestionAsync(
        string sessionId, Hub.HubQuestion question, IReadOnlyDictionary<string, string> answers, CancellationToken ct = default)
    {
        ValidateSessionId(sessionId);
        var updatedInput = question.Input.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(question.Input.GetRawText())!.AsObject()
            : new JsonObject();
        var answerNode = new JsonObject();
        foreach (var (q, a) in answers) answerNode[q] = a;
        updatedInput["answers"] = answerNode;

        var controlResponse = new JsonObject
        {
            ["type"] = "control_response",
            ["response"] = new JsonObject
            {
                ["subtype"] = "success",
                ["request_id"] = question.RequestId,
                ["response"] = new JsonObject
                {
                    ["behavior"] = "allow",
                    ["toolUseID"] = question.ToolUseId,
                    ["tool_name"] = "AskUserQuestion",
                    ["updatedInput"] = updatedInput,
                },
            },
        };
        var body = new JsonObject { ["events"] = new JsonArray(new JsonObject { ["payload"] = controlResponse }) };
        // The /v1/code route takes the cse_ form of the id; the suffix is the same.
        var cseId = "cse_" + sessionId["session_".Length..];
        using var _ = await SendAsync(HttpMethod.Post, $"/v1/code/sessions/{cseId}/events", body, ct);
    }

    public async IAsyncEnumerable<StreamedEvent> StreamEventsAsync(
        string sessionId, long? afterSequence, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ValidateSessionId(sessionId);
        // The /v1/code route takes the cse_ form of the id. from_sequence_num is exclusive: the stream starts after it.
        var cseId = "cse_" + sessionId["session_".Length..];
        var path = $"/v1/code/sessions/{cseId}/events/stream";
        if (afterSequence is { } after) path += $"?from_sequence_num={after}";

        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseAddress, path));
        AddAuth(req);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (afterSequence is { } last) req.Headers.Add("Last-Event-ID", last.ToString());

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        var status = (int)resp.StatusCode;
        if (status is 401 or 403) throw new CloudSessionAuthException(status);
        if (status is < 200 or >= 300)
            throw new CloudSessionApiException(status, await resp.Content.ReadAsStringAsync(ct));

        await using var body = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(body, Encoding.UTF8);
        string? name = null, id = null;
        var data = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null || line.Length == 0)
            {
                if (data.Length > 0 && name == "client_event" && ParseStreamFrame(data.ToString(), id) is { } ev)
                    yield return ev;
                else if (data.Length > 0 && name == "ephemeral_event" && ParseEphemeralFrame(data.ToString()) is { } progress)
                    yield return progress;
                if (line is null) yield break;
                name = id = null;
                data.Clear();
                continue;
            }
            if (line[0] == ':') continue;
            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' ')) value = value[1..];
            switch (field)
            {
                case "event": name = value; break;
                case "id": id = value; break;
                case "data":
                    if (data.Length > 0) data.Append('\n');
                    data.Append(value);
                    break;
            }
        }
    }

    /// <summary>A <c>client_event</c> frame's data, or null when it is not JSON or has no sequence number.</summary>
    private static StreamedEvent? ParseStreamFrame(string data, string? id)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            long seq;
            if (root.TryGetProperty("sequence_num", out var sn) && sn.ValueKind == JsonValueKind.Number && sn.TryGetInt64(out var n))
                seq = n;
            else if (!long.TryParse(id, out seq))
                return null;

            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) return null;
            var ev = ParseEvent(payload);
            if (DateTimeOffset.TryParse(Str(root, "created_at"), out var created)) ev = ev with { CreatedAt = created };
            if (ev.Uuid.Length == 0 && Str(root, "event_id") is { } eventId) ev = ev with { Uuid = eventId };
            return new StreamedEvent(seq, ev);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// An <c>ephemeral_event</c> frame as a progress event, or null for noise and bad JSON. Kept: <c>system</c> <c>task_summary</c>
    /// and <c>post_turn_summary</c>, <c>tool_progress</c> heartbeats, and the main agent's <c>stream_event</c>s that start a message or block or carry text,
    /// plus a subagent's tool-call starts (a running worker's latest tool).
    /// Dropped: <c>status</c>, <c>thinking_tokens</c>, hooks, tool-input and signature deltas, and other subagent output.
    /// </summary>
    private static StreamedEvent? ParseEphemeralFrame(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
                return null;
            var keep = Str(payload, "type") switch
            {
                "tool_progress" => Str(payload, "tool_name") is not null,
                "system" => Str(payload, "subtype") is "task_summary" or "post_turn_summary",
                "stream_event" => payload.TryGetProperty("event", out var ev) && ev.ValueKind == JsonValueKind.Object
                    && (payload.TryGetProperty("parent_tool_use_id", out var parent) && parent.ValueKind == JsonValueKind.String
                        // A subagent's stream: only the start of a tool call of its own, shown as that worker's latest line.
                        ? Str(ev, "type") == "content_block_start" && ev.TryGetProperty("content_block", out var cb) && Str(cb, "type") == "tool_use"
                        : Str(ev, "type") is "message_start" or "content_block_start"
                            || (Str(ev, "type") == "content_block_delta" && ev.TryGetProperty("delta", out var d) && Str(d, "type") == "text_delta")),
                _ => false,
            };
            if (!keep) return null;
            var e = ParseEvent(payload);
            if (DateTimeOffset.TryParse(Str(root, "timestamp"), out var at)) e = e with { CreatedAt = at };
            return new StreamedEvent(0, e, Ephemeral: true);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void ValidateSessionId(string sessionId)
    {
        if (sessionId is null || !SessionIdPattern().IsMatch(sessionId))
            throw new ArgumentException("Session id must match ^session_[A-Za-z0-9]+$.", nameof(sessionId));
    }

    /// <summary>A user turn. The web UI stamps the target session's id on it; an empty id appears to make the worker treat
    /// the text as coming from another session (a peer message) instead of the owner (inferred, not yet confirmed against a
    /// live session, nor which id form the UI sends). Required, so every call site chooses: a new session has no id yet,
    /// so create passes "".</summary>
    private static JsonObject UserEvent(string text, string sessionId) => new()
    {
        ["uuid"] = Guid.NewGuid().ToString(),
        ["session_id"] = sessionId,
        ["type"] = "user",
        ["parent_tool_use_id"] = null,
        ["message"] = new JsonObject { ["role"] = "user", ["content"] = text },
    };

    private async Task<JsonDocument> SendAsync(HttpMethod method, string pathAndQuery, JsonNode? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, new Uri(BaseAddress, pathAndQuery));
        AddAuth(req);
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

    /// <summary>The auth headers every call sends; the beta header is added only where the route needs it.</summary>
    private void AddAuth(HttpRequestMessage req)
    {
        var auth = _credentials.Read();
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        req.Headers.Add("x-organization-uuid", auth.OrganizationUuid);
        req.Headers.Add("anthropic-version", "2023-06-01");
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

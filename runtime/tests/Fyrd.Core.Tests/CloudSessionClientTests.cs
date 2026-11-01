using System.Net;
using System.Text.Json;
using Fyrd.Core.CloudSessions;

namespace Fyrd.Core.Tests;

public sealed class CloudSessionClientTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "fyrd-client-" + Guid.NewGuid());

    public CloudSessionClientTests()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".claude"));
        File.WriteAllText(Path.Combine(_home, ".claude", ".credentials.json"),
            """{"claudeAiOauth":{"accessToken":"tok","expiresAt":1800000000000}}""");
        File.WriteAllText(Path.Combine(_home, ".claude.json"),
            """{"oauthAccount":{"organizationUuid":"org-123"}}""");
    }

    public void Dispose()
    {
        if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
    }

    private CloudSessionClient Client(FakeHandler handler) =>
        new(new HttpClient(handler), new ClaudeCredentials(_home));

    private static CreateSessionRequest SampleRequest() => new(
        "Title", ["fyrd", "peon"], "env_1", "hello", "claude-sonnet-5-5", null,
        new SessionSource("https://github.com/servicetitan/titan-lens-ossie", "main"));

    [Fact]
    public async Task Create_SendsHeadersAndBodyShape()
    {
        var h = new FakeHandler(HttpStatusCode.OK, """{"id":"session_01abc"}""");
        await Client(h).CreateAsync(SampleRequest());

        var r = Assert.Single(h.Requests);
        Assert.Equal(HttpMethod.Post, r.Request.Method);
        Assert.Equal("https://api.anthropic.com/v1/sessions", r.Request.RequestUri!.ToString());
        Assert.Equal("Bearer tok", r.Request.Headers.Authorization!.ToString());
        Assert.Equal("org-123", r.Request.Headers.GetValues("x-organization-uuid").Single());
        Assert.Equal("2023-06-01", r.Request.Headers.GetValues("anthropic-version").Single());
        Assert.Equal("ccr-byoc-2025-07-29", r.Request.Headers.GetValues("anthropic-beta").Single());

        var body = JsonDocument.Parse(r.Body!).RootElement;
        Assert.Equal("env_1", body.GetProperty("environment_id").GetString());
        var ev = body.GetProperty("events")[0];
        Assert.Equal("event", ev.GetProperty("type").GetString());
        var data = ev.GetProperty("data");
        Assert.Equal("user", data.GetProperty("type").GetString());
        Assert.Equal("hello", data.GetProperty("message").GetProperty("content").GetString());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("parent_tool_use_id").ValueKind);
        var ctx = body.GetProperty("session_context");
        Assert.Equal("claude-sonnet-5-5", ctx.GetProperty("model").GetString());
        var src = ctx.GetProperty("sources")[0];
        Assert.Equal("git_repository", src.GetProperty("type").GetString());
        Assert.Equal("https://github.com/servicetitan/titan-lens-ossie", src.GetProperty("url").GetString());
        Assert.False(ctx.TryGetProperty("append_system_prompt", out _));
        Assert.Contains("fyrd", body.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
    }

    [Fact]
    public async Task Create_WithoutSource_SendsEmptySourcesAndSystemPrompt()
    {
        var h = new FakeHandler(HttpStatusCode.OK, """{"id":"session_01abc"}""");
        await Client(h).CreateAsync(SampleRequest() with { Source = null, AppendSystemPrompt = "be brief" });

        var ctx = JsonDocument.Parse(h.Requests[0].Body!).RootElement.GetProperty("session_context");
        Assert.Equal(0, ctx.GetProperty("sources").GetArrayLength());
        Assert.Equal("be brief", ctx.GetProperty("append_system_prompt").GetString());
    }

    [Fact]
    public async Task Create_ParsesSessionId()
    {
        var h = new FakeHandler(HttpStatusCode.OK, """{"id":"session_01abc","title":"T","tags":["fyrd"]}""");
        var s = await Client(h).CreateAsync(SampleRequest());
        Assert.Equal("session_01abc", s.Id);
        Assert.Equal("T", s.Title);
        Assert.Equal(["fyrd"], s.Tags);
    }

    [Fact]
    public async Task Get_ParsesStatusAndPostTurnSummary()
    {
        var h = new FakeHandler(HttpStatusCode.OK, """
            {"id":"session_01abc","title":"T","session_status":"idle","connection_status":"connected",
             "external_metadata":{"post_turn_summary":{"status_category":"completed","needs_action":"none",
             "recent_action":"did it","status_detail":"all good"}}}
            """);
        var s = await Client(h).GetAsync("session_01abc");

        Assert.Equal(HttpMethod.Get, h.Requests[0].Request.Method);
        Assert.EndsWith("/v1/sessions/session_01abc", h.Requests[0].Request.RequestUri!.AbsolutePath);
        Assert.Equal("idle", s.SessionStatus);
        Assert.Equal("connected", s.ConnectionStatus);
        Assert.Equal("completed", s.Summary!.StatusCategory);
        Assert.Equal("none", s.Summary.NeedsAction);
        Assert.Equal("did it", s.Summary.RecentAction);
        Assert.Equal("all good", s.Summary.StatusDetail);
        Assert.Equal("idle", s.Raw.GetProperty("session_status").GetString());
    }

    [Fact]
    public async Task ListEvents_PassesCursorAndExtractsAssistantText()
    {
        var h = new FakeHandler(HttpStatusCode.OK, """
            {"data":[
              {"uuid":"u1","type":"assistant","created_at":"2026-10-29T10:00:00Z",
               "message":{"role":"assistant","content":[{"type":"text","text":"FYRD-"},{"type":"tool_use","id":"x"},{"type":"text","text":"OK"}]}},
              {"uuid":"u2","type":"user","created_at":"2026-10-29T10:00:01Z","message":{"role":"user","content":"hi"}},
              {"uuid":"u3","type":"env_manager_log","created_at":"2026-10-29T10:00:02Z"}],
             "has_more":true,"first_id":"u1","last_id":"u3"}
            """);
        var page = await Client(h).ListEventsAsync("session_01abc", limit: 50, afterId: "u0");

        var uri = h.Requests[0].Request.RequestUri!;
        Assert.Contains("limit=50", uri.Query);
        Assert.Contains("after_id=u0", uri.Query);
        Assert.DoesNotContain("before_id", uri.Query);
        Assert.Equal(3, page.Events.Count);
        Assert.Equal("FYRD-OK", page.Events[0].Text);
        Assert.Equal("hi", page.Events[1].Text);
        Assert.Null(page.Events[2].Text);
        Assert.Equal(DateTimeOffset.Parse("2026-10-29T10:00:00Z"), page.Events[0].CreatedAt);
        Assert.True(page.HasMore);
        Assert.Equal("u1", page.FirstId);
        Assert.Equal("u3", page.LastId);
    }

    [Fact]
    public async Task SendMessage_PostsUnwrappedUserEvent()
    {
        var h = new FakeHandler();
        await Client(h).SendMessageAsync("session_01abc", "go on");

        var r = h.Requests[0];
        Assert.Equal(HttpMethod.Post, r.Request.Method);
        Assert.EndsWith("/v1/sessions/session_01abc/events", r.Request.RequestUri!.AbsolutePath);
        var ev = JsonDocument.Parse(r.Body!).RootElement.GetProperty("events")[0];
        Assert.Equal("user", ev.GetProperty("type").GetString());
        Assert.Equal("go on", ev.GetProperty("message").GetProperty("content").GetString());
        // An empty session_id reads as a peer message to the worker; the UI sends the target's id.
        Assert.Equal("session_01abc", ev.GetProperty("session_id").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Status401Or403_ThrowsAuthException_WithoutToken(HttpStatusCode code)
    {
        var h = new FakeHandler(code, "denied");
        var ex = await Assert.ThrowsAsync<CloudSessionAuthException>(() => Client(h).GetAsync("session_01abc"));
        Assert.DoesNotContain("tok", ex.Message);
    }

    [Fact]
    public async Task Status500_ThrowsApiException_WithStatus()
    {
        var h = new FakeHandler(HttpStatusCode.InternalServerError, "boom");
        var ex = await Assert.ThrowsAsync<CloudSessionApiException>(() => Client(h).GetAsync("session_01abc"));
        Assert.Equal(500, ex.Status);
        Assert.Equal("boom", ex.Body);
        Assert.DoesNotContain("tok", ex.Message);
    }

    [Theory]
    [InlineData("session_1/../x")]
    [InlineData("abc")]
    [InlineData("")]
    public async Task Get_RejectsPathInjection(string id)
    {
        var h = new FakeHandler();
        await Assert.ThrowsAsync<ArgumentException>(() => Client(h).GetAsync(id));
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task Create_WithPermissionMode_PrependsSetModeEvent()
    {
        var h = new FakeHandler(HttpStatusCode.OK, """{"id":"session_01abc"}""");
        await Client(h).CreateAsync(SampleRequest() with { PermissionMode = "auto" });

        var events = JsonDocument.Parse(h.Requests[0].Body!).RootElement.GetProperty("events");
        Assert.Equal(2, events.GetArrayLength());
        Assert.Equal("event", events[0].GetProperty("type").GetString());
        var data = events[0].GetProperty("data");
        Assert.Equal("control_request", data.GetProperty("type").GetString());
        Assert.Equal("set_permission_mode", data.GetProperty("request").GetProperty("subtype").GetString());
        Assert.Equal("auto", data.GetProperty("request").GetProperty("mode").GetString());
        Assert.StartsWith("set-mode-", data.GetProperty("request_id").GetString());
        Assert.Equal("user", events[1].GetProperty("data").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Create_WithoutPermissionMode_HasOnlyUserEvent()
    {
        var h = new FakeHandler(HttpStatusCode.OK, """{"id":"session_01abc"}""");
        await Client(h).CreateAsync(SampleRequest());

        var events = JsonDocument.Parse(h.Requests[0].Body!).RootElement.GetProperty("events");
        Assert.Equal(1, events.GetArrayLength());
        Assert.Equal("user", events[0].GetProperty("data").GetProperty("type").GetString());
    }

    [Fact]
    public async Task ListByTag_SendsTagsQuery_AndFollowsPages()
    {
        var pages = new Queue<string>(
        [
            """{"data":[{"id":"session_1"},{"id":"session_2"}],"has_more":true,"last_id":"session_2"}""",
            """{"data":[{"id":"session_3","tags":["fyrd-spec-x"]}],"has_more":false,"last_id":"session_3"}""",
        ]);
        var h = new FakeHandler(HttpStatusCode.OK, () => pages.Dequeue());
        var result = await Client(h).ListByTagAsync("fyrd-spec-x");

        Assert.Equal(["session_1", "session_2", "session_3"], result.Select(s => s.Id));
        Assert.Equal(2, h.Requests.Count);
        Assert.All(h.Requests, r => Assert.StartsWith("/v1/sessions?tags=fyrd-spec-x", r.Request.RequestUri!.PathAndQuery));
        Assert.DoesNotContain("after_id", h.Requests[0].Request.RequestUri!.Query);
        Assert.Contains("after_id=session_2", h.Requests[1].Request.RequestUri!.Query);
    }

    [Fact]
    public async Task ListByTag_StopsAfterTwentyPages()
    {
        var h = new FakeHandler(HttpStatusCode.OK, () => """{"data":[{"id":"session_1"}],"has_more":true,"last_id":"session_1"}""");
        var result = await Client(h).ListByTagAsync("t");
        Assert.Equal(20, h.Requests.Count);
        Assert.Equal(20, result.Count);
    }

    [Fact]
    public async Task Interrupt_PostsControlRequestEvent()
    {
        var h = new FakeHandler();
        await Client(h).InterruptAsync("session_1");

        var r = h.Requests[0];
        Assert.Equal(HttpMethod.Post, r.Request.Method);
        Assert.Equal("/v1/sessions/session_1/events", r.Request.RequestUri!.AbsolutePath);
        var ev = JsonDocument.Parse(r.Body!).RootElement.GetProperty("events")[0];
        Assert.Equal("control_request", ev.GetProperty("type").GetString());
        Assert.False(string.IsNullOrEmpty(ev.GetProperty("request_id").GetString()));
        Assert.Equal("interrupt", ev.GetProperty("request").GetProperty("subtype").GetString());
        Assert.True(ev.GetProperty("request").GetProperty("cancel_queued").GetBoolean());
    }

    [Fact]
    public async Task Archive_PostsToCodeSessionsPath()
    {
        var h = new FakeHandler();
        await Client(h).ArchiveAsync("session_1");

        var r = h.Requests[0];
        Assert.Equal(HttpMethod.Post, r.Request.Method);
        Assert.Equal("/v1/code/sessions/session_1/archive", r.Request.RequestUri!.AbsolutePath);
        Assert.Equal("{}", r.Body);
    }

    [Fact]
    public async Task Get_NonJson2xx_ThrowsApiException()
    {
        var h = new FakeHandler(HttpStatusCode.OK, "<html>");
        var ex = await Assert.ThrowsAsync<CloudSessionApiException>(() => Client(h).GetAsync("session_1"));
        Assert.Equal(200, ex.Status);
        Assert.Equal("<non-JSON body>", ex.Body);
        Assert.DoesNotContain("tok", ex.Message);
    }

    [Fact]
    public async Task AnswerQuestion_PostsSpikeShape()
    {
        var input = JsonDocument.Parse(
            """{"questions":[{"question":"Pick a fruit","header":"Fruit","multiSelect":false,"options":[{"label":"Apple"},{"label":"Banana"}]}]}""")
            .RootElement.Clone();
        var q = new Fyrd.Core.Hub.HubQuestion("q1", "session_01abc", "builder-x", "Pick a fruit", ["Apple", "Banana"], false,
            DateTimeOffset.UnixEpoch, "toolu_9", "req_7", input);
        var h = new FakeHandler(HttpStatusCode.OK, "{}");
        await Client(h).AnswerQuestionAsync("session_01abc", q, new Dictionary<string, string> { ["Pick a fruit"] = "Banana" });

        var r = Assert.Single(h.Requests);
        Assert.Equal(HttpMethod.Post, r.Request.Method);
        Assert.Equal("https://api.anthropic.com/v1/code/sessions/cse_01abc/events", r.Request.RequestUri!.ToString());
        Assert.Equal("Bearer tok", r.Request.Headers.Authorization!.ToString());

        var ev = Assert.Single(JsonDocument.Parse(r.Body!).RootElement.GetProperty("events").EnumerateArray());
        var payload = ev.GetProperty("payload");
        Assert.Equal("control_response", payload.GetProperty("type").GetString());
        var resp = payload.GetProperty("response");
        Assert.Equal("success", resp.GetProperty("subtype").GetString());
        Assert.Equal("req_7", resp.GetProperty("request_id").GetString());
        var inner = resp.GetProperty("response");
        Assert.Equal("allow", inner.GetProperty("behavior").GetString());
        Assert.Equal("toolu_9", inner.GetProperty("toolUseID").GetString());
        Assert.Equal("AskUserQuestion", inner.GetProperty("tool_name").GetString());
        var updated = inner.GetProperty("updatedInput");
        Assert.Equal("Pick a fruit", updated.GetProperty("questions")[0].GetProperty("question").GetString());
        Assert.Equal(2, updated.GetProperty("questions")[0].GetProperty("options").GetArrayLength());
        Assert.Equal("Banana", updated.GetProperty("answers").GetProperty("Pick a fruit").GetString());
    }

    [Fact]
    public async Task AnswerQuestion_RejectsBadSessionId()
    {
        var q = new Fyrd.Core.Hub.HubQuestion("q1", "x", "t", "Q", [], false, DateTimeOffset.UnixEpoch, "t", "r",
            JsonDocument.Parse("{}").RootElement.Clone());
        var h = new FakeHandler();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Client(h).AnswerQuestionAsync("cse_../x", q, new Dictionary<string, string>()));
        Assert.Empty(h.Requests);
    }

    [Fact]
    public void Client_ImplementsICloudSessions() =>
        Assert.IsAssignableFrom<ICloudSessions>(Client(new FakeHandler()));

    private static async Task<List<StreamedEvent>> Drain(IAsyncEnumerable<StreamedEvent> stream)
    {
        var list = new List<StreamedEvent>();
        await foreach (var e in stream) list.Add(e);
        return list;
    }

    [Fact]
    public async Task StreamEvents_YieldsOnlyTheEphemeralEventsThatShowProgress()
    {
        static string Frame(string payload) => $"event: ephemeral_event\ndata: {{\"event_type\":\"x\",\"payload\":{payload},\"timestamp\":\"2026-11-01T10:00:02Z\"}}\n\n";
        var sse = string.Concat(
            Frame("""{"type":"system","subtype":"task_summary","detail":"Running grep"}"""),
            Frame("""{"type":"stream_event","event":{"type":"message_start","message":{"content":[]}}}"""),
            Frame("""{"type":"stream_event","event":{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","name":"Read"}}}"""),
            Frame("""{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello"}}}"""),
            Frame("""{"type":"system","subtype":"post_turn_summary","status_detail":"done"}"""),
            Frame("""{"type":"tool_progress","tool_name":"Bash","elapsed_time_seconds":30,"heartbeat":true}"""),
            // Noise, all dropped:
            Frame("""{"type":"system","subtype":"status","status":"requesting"}"""),
            Frame("""{"type":"system","subtype":"thinking_tokens","estimated_tokens":5}"""),
            Frame("""{"type":"system","subtype":"hook_started"}"""),
            Frame("""{"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{"}}}"""),
            Frame("""{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"x"}}}"""),
            Frame("""{"type":"stream_event","event":{"type":"content_block_stop","index":0}}"""),
            Frame("""{"type":"stream_event","parent_tool_use_id":"toolu_sub","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"from a subagent"}}}"""));
        var events = await Drain(Client(new FakeHandler(HttpStatusCode.OK, sse)).StreamEventsAsync("session_01abc", null));

        Assert.Equal(6, events.Count);
        Assert.All(events, e => { Assert.True(e.Ephemeral); Assert.Equal(0, e.Sequence); });
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T10:00:02Z"), events[0].Event.CreatedAt);
        Assert.Equal("Running grep", events[0].Event.Raw.GetProperty("detail").GetString());
        Assert.Equal("Hello", events[3].Event.Raw.GetProperty("event").GetProperty("delta").GetProperty("text").GetString());
    }

    [Fact]
    public async Task StreamEvents_ParsesClientEventsOnly_NoBetaHeader()
    {
        const string sse = """
            event: session_update
            data: {"connection_status":"connected"}

            : comment

            event: client_event
            id: 5
            data: {"event_id":"ev_5","event_type":"user","sequence_num":5,"created_at":"2026-11-01T10:00:00Z","payload":{"type":"user","message":{"content":"hi"}}}

            event: ephemeral_event
            data: {"event_type":"stream_event","payload":{"type":"stream_event"}}

            event: client_event
            id: 6
            data: {"event_id":"ev_6","created_at":"2026-11-01T10:00:01Z",
            data: "payload":{"type":"assistant","uuid":"a6","message":{"content":[{"type":"text","text":"yo"}]}}}

            event: client_event
            id: 7
            data: not json
            """;
        var h = new FakeHandler(HttpStatusCode.OK, sse);
        var events = await Drain(Client(h).StreamEventsAsync("session_01abc", null));

        Assert.Equal([5L, 6L], events.Select(e => e.Sequence));
        Assert.Equal(("user", "hi", "ev_5"), (events[0].Event.Type, events[0].Event.Text, events[0].Event.Uuid));
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T10:00:00Z"), events[0].Event.CreatedAt);
        Assert.Equal(("assistant", "yo", "a6"), (events[1].Event.Type, events[1].Event.Text, events[1].Event.Uuid));

        var r = Assert.Single(h.Requests).Request;
        Assert.Equal("https://api.anthropic.com/v1/code/sessions/cse_01abc/events/stream", r.RequestUri!.ToString());
        Assert.Equal("text/event-stream", r.Headers.Accept.Single().MediaType);
        Assert.Equal("Bearer tok", r.Headers.Authorization!.ToString());
        Assert.Equal("org-123", r.Headers.GetValues("x-organization-uuid").Single());
        Assert.False(r.Headers.Contains("anthropic-beta"));
    }

    [Fact]
    public async Task StreamEvents_AfterSequence_SendsFromSequenceNum()
    {
        var h = new FakeHandler(HttpStatusCode.OK, "");
        Assert.Empty(await Drain(Client(h).StreamEventsAsync("session_01abc", 42)));
        var r = Assert.Single(h.Requests).Request;
        Assert.Equal("https://api.anthropic.com/v1/code/sessions/cse_01abc/events/stream?from_sequence_num=42", r.RequestUri!.ToString());
        Assert.Equal("42", r.Headers.GetValues("Last-Event-ID").Single());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task StreamEvents_AuthFailure_Throws(HttpStatusCode status) =>
        await Assert.ThrowsAsync<CloudSessionAuthException>(() =>
            Drain(Client(new FakeHandler(status, "{}")).StreamEventsAsync("session_01abc", null)));

    [Fact]
    public async Task StreamEvents_ServerError_ThrowsApiException()
    {
        var e = await Assert.ThrowsAsync<CloudSessionApiException>(() =>
            Drain(Client(new FakeHandler(HttpStatusCode.BadGateway, "boom")).StreamEventsAsync("session_01abc", null)));
        Assert.Equal(502, e.Status);
    }

    [Fact]
    public async Task StreamEvents_BadSessionId_Throws() =>
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Drain(Client(new FakeHandler()).StreamEventsAsync("../x", null)));
}

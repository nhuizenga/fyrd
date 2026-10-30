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
    public void Client_ImplementsICloudSessions() =>
        Assert.IsAssignableFrom<ICloudSessions>(Client(new FakeHandler()));
}

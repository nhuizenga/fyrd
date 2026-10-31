using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fyrd.Core.CloudSessions;
using Fyrd.Core.Config;
using Fyrd.Core.Hub;
using Microsoft.Extensions.DependencyInjection;

namespace Fyrd.Server.Tests;

public class HubApiTests
{
    private const string SessionId = "session_hub1";

    private static async Task<JsonElement> ReadJson(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private static HubSessionView View(string id = SessionId) =>
        new(id, "builder-abc", "builder", "running", null, null, null, null, ["fyrd-hub", "fyrd-role-builder"]);

    private static HubQuestion Question(string id = "q1") => new(
        id, SessionId, "builder-abc", "Pick a fruit", ["Apple", "Banana"], false, DateTimeOffset.UnixEpoch,
        "toolu_1", "req_1",
        JsonDocument.Parse("""{"questions":[{"question":"Pick a fruit","header":"Fruit","multiSelect":false,"options":[{"label":"Apple"},{"label":"Banana"}]}]}""").RootElement.Clone());

    private static void Seed(FyrdFactory f, bool withQuestion = false) =>
        f.Services.GetRequiredService<HubState>().Publish([View()], withQuestion ? [Question()] : []);

    private static void SetEnvironment(FyrdFactory f)
    {
        var config = f.Services.GetRequiredService<ConfigStore>();
        config.Save(config.Load() with { EnvironmentId = "env_test1" });
    }

    [Fact]
    public async Task State_Empty_Ok()
    {
        using var f = new FyrdFactory();
        var r = await f.CreateClient().GetAsync("/api/hub/state");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await ReadJson(r);
        Assert.Equal(0, body.GetProperty("sessions").GetArrayLength());
        Assert.Equal(0, body.GetProperty("questions").GetArrayLength());
        Assert.False(body.GetProperty("signedOut").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("lastError").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("lastPollAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("controller").ValueKind);
    }

    [Fact]
    public async Task State_WithController_ShowsIdAndUrl()
    {
        using var f = new FyrdFactory();
        SetEnvironment(f);
        var c = f.CreateClient();
        await c.PostAsync("/api/hub/controller", null);
        var ctl = (await ReadJson(await c.GetAsync("/api/hub/state"))).GetProperty("controller");
        Assert.Equal("created-1", ctl.GetProperty("sessionId").GetString());
        Assert.Equal("https://claude.ai/code/created-1", ctl.GetProperty("url").GetString());
    }

    [Fact]
    public async Task Controller_Post_CreatesOnce()
    {
        using var f = new FyrdFactory();
        SetEnvironment(f);
        var c = f.CreateClient();
        var first = await c.PostAsync("/api/hub/controller", null);
        var second = await c.PostAsync("/api/hub/controller", null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Single(f.Cloud.Created);
        Assert.Equal("created-1", (await ReadJson(second)).GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task Controller_NoEnvironment_400()
    {
        using var f = new FyrdFactory();
        var r = await f.CreateClient().PostAsync("/api/hub/controller", null);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("Set a cloud environment in Settings first.", (await ReadJson(r)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Controller_Recreate_ArchivesAndCreates()
    {
        using var f = new FyrdFactory();
        SetEnvironment(f);
        var c = f.CreateClient();
        await c.PostAsync("/api/hub/controller", null);
        var r = await c.PostAsync("/api/hub/controller/recreate", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("created-2", (await ReadJson(r)).GetProperty("sessionId").GetString());
        Assert.Equal(["created-1"], f.Cloud.Archived);
    }

    [Fact]
    public async Task Message_UnknownSession_404()
    {
        using var f = new FyrdFactory();
        var r = await f.CreateClient().PostAsJsonAsync("/api/hub/sessions/session_other/message", new { text = "hi" });
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Empty(f.Cloud.Sent);
    }

    [Fact]
    public async Task Message_HubSession_SendsText()
    {
        using var f = new FyrdFactory();
        Seed(f);
        var r = await f.CreateClient().PostAsJsonAsync($"/api/hub/sessions/{SessionId}/message", new { text = "hello" });
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal((SessionId, "hello"), Assert.Single(f.Cloud.Sent));
    }

    [Fact]
    public async Task Message_EmptyOrTooLong_400()
    {
        using var f = new FyrdFactory();
        Seed(f);
        var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await c.PostAsJsonAsync($"/api/hub/sessions/{SessionId}/message", new { text = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await c.PostAsJsonAsync($"/api/hub/sessions/{SessionId}/message", new { text = new string('x', 20_001) })).StatusCode);
        Assert.Empty(f.Cloud.Sent);
    }

    [Fact]
    public async Task PauseResume_HubSession()
    {
        using var f = new FyrdFactory();
        Seed(f);
        var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsync($"/api/hub/sessions/{SessionId}/pause", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsync($"/api/hub/sessions/{SessionId}/resume", null)).StatusCode);
        Assert.Equal([SessionId], f.Cloud.Interrupted);
        Assert.Equal((SessionId, "Continue where you left off."), Assert.Single(f.Cloud.Sent));
    }

    [Fact]
    public async Task Messages_ReturnsUserAndAgentText_FromPollWindow()
    {
        using var f = new FyrdFactory();
        Seed(f);
        var empty = Core.Tests.FakeCloudSessions.Empty;
        f.Services.GetRequiredService<HubState>().PublishRecent(new Dictionary<string, IReadOnlyList<SessionEvent>>
        {
            [SessionId] =
            [
                new("e1", "user", DateTimeOffset.UnixEpoch, "do it", empty),
                new("e2", "system", DateTimeOffset.UnixEpoch, null, empty),
                new("e3", "user", DateTimeOffset.UnixEpoch, "", empty),
                new("e4", "assistant", DateTimeOffset.UnixEpoch, "done", empty),
            ],
        });
        var list = (await ReadJson(await f.CreateClient().GetAsync($"/api/hub/sessions/{SessionId}/messages")))
            .EnumerateArray().ToList();
        Assert.Equal(2, list.Count);
        Assert.Equal("user", list[0].GetProperty("role").GetString());
        Assert.Equal("do it", list[0].GetProperty("text").GetString());
        Assert.Equal("agent", list[1].GetProperty("role").GetString());
        Assert.True(list[1].TryGetProperty("at", out _));
        Assert.Empty(f.Cloud.EventAfterIds); // served from memory, no cloud calls
    }

    [Fact]
    public async Task Messages_KeepsLast50()
    {
        using var f = new FyrdFactory();
        Seed(f);
        var events = Enumerable.Range(0, 120)
            .Select(i => new SessionEvent("e" + i, "assistant", DateTimeOffset.UnixEpoch, "m" + i, Core.Tests.FakeCloudSessions.Empty))
            .ToList();
        f.Services.GetRequiredService<HubState>().PublishRecent(
            new Dictionary<string, IReadOnlyList<SessionEvent>> { [SessionId] = events });
        var list = (await ReadJson(await f.CreateClient().GetAsync($"/api/hub/sessions/{SessionId}/messages")))
            .EnumerateArray().ToList();
        Assert.Equal(50, list.Count);
        Assert.Equal("m70", list[0].GetProperty("text").GetString());
        Assert.Equal("m119", list[^1].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Messages_WindowNotPopulated_Empty()
    {
        using var f = new FyrdFactory();
        Seed(f);
        var r = await f.CreateClient().GetAsync($"/api/hub/sessions/{SessionId}/messages");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(0, (await ReadJson(r)).GetArrayLength());
        Assert.Empty(f.Cloud.EventAfterIds);
    }

    [Fact]
    public async Task Persona_DeleteController_409()
    {
        using var f = new FyrdFactory();
        var r = await f.CreateClient().DeleteAsync("/api/hub/personas/controller");
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
    }

    [Fact]
    public async Task Persona_PutGetDelete()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        var put = await c.PutAsJsonAsync("/api/hub/personas/scout",
            new { id = "", name = "Scout", model = "claude-haiku-5-5", definition = "Look around.", locked = false });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("scout", (await ReadJson(put)).GetProperty("id").GetString());
        var got = await ReadJson(await c.GetAsync("/api/hub/personas/scout"));
        Assert.Equal("Scout", got.GetProperty("name").GetString());
        Assert.Contains((await ReadJson(await c.GetAsync("/api/hub/personas"))).EnumerateArray(),
            p => p.GetProperty("id").GetString() == "scout");
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync("/api/hub/personas/scout")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/hub/personas/scout")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.DeleteAsync("/api/hub/personas/scout")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.DeleteAsync("/api/hub/personas/Bad_Id")).StatusCode);
    }

    [Fact]
    public async Task Memory_AddEditDelete_RoundTrip()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        var add = await c.PostAsJsonAsync("/api/hub/memory/shared", new { text = "Use pnpm." });
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        var entry = await ReadJson(add);
        Assert.Equal("owner", entry.GetProperty("source").GetString());
        var id = entry.GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.NoContent,
            (await c.PutAsJsonAsync($"/api/hub/memory/shared/{id}", new { text = "Use npm." })).StatusCode);
        var scopes = (await ReadJson(await c.GetAsync("/api/hub/memory"))).GetProperty("scopes").EnumerateArray().ToList();
        Assert.Equal("shared", scopes[0].GetProperty("scope").GetString());
        Assert.Equal("Shared", scopes[0].GetProperty("label").GetString());
        Assert.Equal("Use npm.", scopes[0].GetProperty("entries")[0].GetProperty("text").GetString());
        var controller = Assert.Single(scopes, s => s.GetProperty("scope").GetString() == "controller");
        Assert.Equal("Controller", controller.GetProperty("label").GetString());
        Assert.Equal(0, controller.GetProperty("entries").GetArrayLength());

        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/hub/memory/shared/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.DeleteAsync($"/api/hub/memory/shared/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await c.PostAsJsonAsync("/api/hub/memory/Bad_Scope", new { text = "x" })).StatusCode);
    }

    [Fact]
    public async Task Answer_Supported_CallsCloud()
    {
        using var f = new FyrdFactory();
        Seed(f, withQuestion: true);
        var r = await f.CreateClient().PostAsJsonAsync("/api/hub/questions/q1/answer", new { choice = "Banana" });
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        var (sessionId, question, answers) = Assert.Single(f.Cloud.Answered);
        Assert.Equal(SessionId, sessionId);
        Assert.Equal("q1", question.Id);
        Assert.Equal("Banana", answers["Pick a fruit"]);
        Assert.Empty(f.Services.GetRequiredService<HubState>().Questions);
    }

    [Fact]
    public async Task Answer_FreeText_CallsCloud()
    {
        using var f = new FyrdFactory();
        Seed(f, withQuestion: true);
        var r = await f.CreateClient().PostAsJsonAsync("/api/hub/questions/q1/answer", new { text = "Mango" });
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal("Mango", Assert.Single(f.Cloud.Answered).Answers["Pick a fruit"]);
    }

    [Fact]
    public async Task Answer_NotPending_409()
    {
        using var f = new FyrdFactory();
        Seed(f);
        var r = await f.CreateClient().PostAsJsonAsync("/api/hub/questions/q1/answer", new { choice = "Banana" });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("This question is no longer waiting for an answer.", (await ReadJson(r)).GetProperty("detail").GetString());
        Assert.Empty(f.Cloud.Answered);
    }

    [Fact]
    public async Task Answer_ChoiceNotInOptions_400()
    {
        using var f = new FyrdFactory();
        Seed(f, withQuestion: true);
        var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await c.PostAsJsonAsync("/api/hub/questions/q1/answer", new { choice = "Cherry" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await c.PostAsJsonAsync("/api/hub/questions/q1/answer", new { choice = "Apple", text = "also" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await c.PostAsJsonAsync("/api/hub/questions/q1/answer", new { })).StatusCode);
        Assert.Empty(f.Cloud.Answered);
        Assert.Single(f.Services.GetRequiredService<HubState>().Questions);
    }

    private static HubQuestion Sibling(string id, string text) => Question(id) with { Question = text };

    [Fact]
    public async Task Answer_TwoQuestionRequest_SendsOnceWithBoth()
    {
        using var f = new FyrdFactory();
        var state = f.Services.GetRequiredService<HubState>();
        state.Publish([View()], [Sibling("q1", "Pick a fruit"), Sibling("q2", "Pick a colour")]);
        var c = f.CreateClient();

        var first = await c.PostAsJsonAsync("/api/hub/questions/q1/answer", new { choice = "Banana" });
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(1, (await ReadJson(first)).GetProperty("waitingFor").GetInt32());
        Assert.Empty(f.Cloud.Answered);

        var second = await c.PostAsJsonAsync("/api/hub/questions/q2/answer", new { text = "Teal" });
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        var answered = Assert.Single(f.Cloud.Answered);
        Assert.Equal(2, answered.Answers.Count);
        Assert.Equal("Banana", answered.Answers["Pick a fruit"]);
        Assert.Equal("Teal", answered.Answers["Pick a colour"]);
        Assert.Empty(state.Questions);
    }

    [Fact]
    public async Task Answer_Concurrent_OnlyOneSent()
    {
        using var f = new FyrdFactory();
        Seed(f, withQuestion: true);
        var c = f.CreateClient();
        var results = await Task.WhenAll(
            c.PostAsJsonAsync("/api/hub/questions/q1/answer", new { choice = "Apple" }),
            c.PostAsJsonAsync("/api/hub/questions/q1/answer", new { choice = "Banana" }));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.NoContent);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Single(f.Cloud.Answered);
    }

    [Fact]
    public async Task Answer_ThenStalePublish_StaysGone()
    {
        using var f = new FyrdFactory();
        Seed(f, withQuestion: true);
        var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.NoContent,
            (await c.PostAsJsonAsync("/api/hub/questions/q1/answer", new { choice = "Banana" })).StatusCode);
        f.Services.GetRequiredService<HubState>().Publish([View()], [Question()]); // poll pass that started before the answer
        Assert.Equal(0, (await ReadJson(await c.GetAsync("/api/hub/state"))).GetProperty("questions").GetArrayLength());
        Assert.Equal(HttpStatusCode.Conflict,
            (await c.PostAsJsonAsync("/api/hub/questions/q1/answer", new { choice = "Apple" })).StatusCode);
        Assert.Single(f.Cloud.Answered);
    }

    [Fact]
    public async Task Answer_CloudFails_502_ThenRetrySucceeds()
    {
        using var f = new FyrdFactory();
        Seed(f, withQuestion: true);
        var c = f.CreateClient();
        f.Cloud.ThrowOnNextCall(new Fyrd.Core.CloudSessions.CloudSessionApiException(500, "boom"));
        Assert.Equal(HttpStatusCode.BadGateway,
            (await c.PostAsJsonAsync("/api/hub/questions/q1/answer", new { choice = "Banana" })).StatusCode);
        Assert.Single(f.Services.GetRequiredService<HubState>().Questions);
        Assert.Equal(HttpStatusCode.NoContent,
            (await c.PostAsJsonAsync("/api/hub/questions/q1/answer", new { choice = "Banana" })).StatusCode);
        Assert.Single(f.Cloud.Answered);
    }

    [Fact]
    public async Task Answer_ThenFailedPoll_ThenPending_SecondAnswer409()
    {
        using var f = new FyrdFactory();
        Seed(f, withQuestion: true);
        var state = f.Services.GetRequiredService<HubState>();
        var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.NoContent,
            (await c.PostAsJsonAsync("/api/hub/questions/q1/answer", new { choice = "Banana" })).StatusCode);

        state.Publish([View()], [], new HashSet<string> { SessionId }); // the session's poll failed
        state.Publish([View()], [Question()]); // the next poll still sees it pending
        Assert.Empty(state.Questions);
        Assert.Equal(HttpStatusCode.Conflict,
            (await c.PostAsJsonAsync("/api/hub/questions/q1/answer", new { choice = "Apple" })).StatusCode);
        Assert.Single(f.Cloud.Answered);
    }

    [Fact]
    public async Task Answer_Cancelled_KeepsClaim()
    {
        using var f = new FyrdFactory();
        Seed(f, withQuestion: true);
        var state = f.Services.GetRequiredService<HubState>();
        f.Cloud.ThrowOnNextCall(new TaskCanceledException("timed out"));
        var r = await f.CreateClient().PostAsJsonAsync("/api/hub/questions/q1/answer", new { choice = "Banana" });
        Assert.NotEqual(HttpStatusCode.NoContent, r.StatusCode);
        Assert.False(state.TryClaim(Question().RequestId)); // may have been delivered: never released
    }

    [Fact]
    public async Task Config_SessionCap_RoundTrip()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        var put = await c.PutAsJsonAsync("/api/config", new { sessionCap = 7 });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(7, (await ReadJson(await c.GetAsync("/api/config"))).GetProperty("sessionCap").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/api/config", new { sessionCap = 101 })).StatusCode);
    }
}

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

    private static async Task<HttpResponseMessage> Adopt(FyrdFactory f, HttpClient c, string id = "session_ctl1")
    {
        f.Cloud.SetSession(Core.Tests.FakeCloudSessions.Session(id));
        return await c.PostAsJsonAsync("/api/hub/controller/adopt", new { sessionId = id });
    }

    [Fact]
    public async Task State_WithController_ShowsIdAndUrl()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        await Adopt(f, c);
        f.Services.GetRequiredService<HubState>().Publish([View("session_ctl1")], []);
        var ctl = (await ReadJson(await c.GetAsync("/api/hub/state"))).GetProperty("controller");
        Assert.Equal("session_ctl1", ctl.GetProperty("sessionId").GetString());
        Assert.Equal("https://claude.ai/code/session_ctl1", ctl.GetProperty("url").GetString());
    }

    [Fact]
    public async Task State_StoredControllerNotLive_ControllerNull()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        await Adopt(f, c);
        // A poll after the adoption did not list the stored Controller (archived elsewhere): no dead link.
        var state = f.Services.GetRequiredService<HubState>();
        state.Publish([View()], []);
        state.LastPollAt = DateTimeOffset.UtcNow.AddMinutes(1);
        var body = await ReadJson(await c.GetAsync("/api/hub/state"));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("controller").ValueKind);
    }

    [Fact]
    public async Task State_JustAdopted_ShowsControllerBeforeAnyPoll()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        await Adopt(f, c);
        var ctl = (await ReadJson(await c.GetAsync("/api/hub/state"))).GetProperty("controller");
        Assert.Equal("session_ctl1", ctl.GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task State_AdoptedAfterLastPoll_ShowsController()
    {
        using var f = new FyrdFactory();
        var state = f.Services.GetRequiredService<HubState>();
        state.Publish([View()], []);
        state.LastPollAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var c = f.CreateClient();
        await Adopt(f, c);
        var ctl = (await ReadJson(await c.GetAsync("/api/hub/state"))).GetProperty("controller");
        Assert.Equal("session_ctl1", ctl.GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task Adopt_RequestsAnImmediatePoll()
    {
        using var f = new FyrdFactory();
        var state = f.Services.GetRequiredService<HubState>();
        Assert.False(state.PollRequested.IsCompleted);
        await Adopt(f, f.CreateClient());
        Assert.True(state.PollRequested.IsCompleted);
    }

    [Fact]
    public async Task Adopt_FyrdPeasant_400()
    {
        using var f = new FyrdFactory();
        f.Cloud.SetSession(Core.Tests.FakeCloudSessions.Session("session_p1", tags: ["fyrd-hub", "fyrd-role-builder"]));
        var r = await f.CreateClient().PostAsJsonAsync("/api/hub/controller/adopt", new { sessionId = "session_p1" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("That's a Fyrd peasant session. Create a fresh session on claude.ai and paste its id.",
            (await ReadJson(r)).GetProperty("detail").GetString());
        Assert.Empty(f.Cloud.Sent);
    }

    [Fact]
    public async Task Adopt_Ok_SendsPromptAndReturnsInfo()
    {
        using var f = new FyrdFactory();
        SetEnvironment(f);
        var r = await Adopt(f, f.CreateClient());
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await ReadJson(r);
        Assert.Equal("session_ctl1", body.GetProperty("sessionId").GetString());
        Assert.Equal("https://claude.ai/code/session_ctl1", body.GetProperty("url").GetString());
        var sent = Assert.Single(f.Cloud.Sent);
        Assert.Equal("session_ctl1", sent.SessionId);
        Assert.StartsWith("You are the Fyrd Controller.", sent.Text);
        Assert.Contains("environment_id: env_test1", sent.Text);
        Assert.Empty(f.Cloud.Created);
        Assert.Equal("session_ctl1", f.Services.GetRequiredService<ControllerManager>().CurrentId);
    }

    [Theory]
    [InlineData("not-a-session")]
    [InlineData("")]
    public async Task Adopt_BadId_400(string id)
    {
        using var f = new FyrdFactory();
        var r = await f.CreateClient().PostAsJsonAsync("/api/hub/controller/adopt", new { sessionId = id });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("session_", (await ReadJson(r)).GetProperty("detail").GetString());
        Assert.Empty(f.Cloud.Sent);
    }

    [Fact]
    public async Task Adopt_NoBody_400()
    {
        using var f = new FyrdFactory();
        var r = await f.CreateClient().PostAsync("/api/hub/controller/adopt",
            new StringContent("", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Adopt_Unavailable_400()
    {
        using var f = new FyrdFactory();
        f.Cloud.SetSession(Core.Tests.FakeCloudSessions.Session("session_gone", "archived"));
        var r = await f.CreateClient().PostAsJsonAsync("/api/hub/controller/adopt", new { sessionId = "session_gone" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("That session isn't available. Create a new one on claude.ai and paste its id.",
            (await ReadJson(r)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Adopt_PersonaMissing_400()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        await c.GetAsync("/api/hub/personas"); // seeds
        File.WriteAllText(Path.Combine(f.Home.Paths.HubPersonasDir, "controller.json"), "{ not json");
        var r = await Adopt(f, c);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("The Controller persona is missing.", (await ReadJson(r)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Controller_Find_Live_200()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        await Adopt(f, c);
        var r = await c.PostAsync("/api/hub/controller", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("session_ctl1", (await ReadJson(r)).GetProperty("sessionId").GetString());
        Assert.Empty(f.Cloud.Created);
    }

    [Fact]
    public async Task Controller_Find_None_404()
    {
        using var f = new FyrdFactory();
        var r = await f.CreateClient().PostAsync("/api/hub/controller", null);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal("No Controller yet. Create a session on claude.ai and adopt it.",
            (await ReadJson(r)).GetProperty("detail").GetString());
        Assert.Empty(f.Cloud.Created);
    }

    [Fact]
    public async Task Controller_Find_Archived_404AndCleared()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        await Adopt(f, c);
        f.Cloud.SetSession(Core.Tests.FakeCloudSessions.Session("session_ctl1", "archived"));
        var r = await c.PostAsync("/api/hub/controller", null);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Null(f.Services.GetRequiredService<ControllerManager>().CurrentId);
    }

    [Fact]
    public async Task Controller_Retire_ArchivesAndClears_204()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        await Adopt(f, c);
        var r = await c.PostAsync("/api/hub/controller/retire", null);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal(["session_ctl1"], f.Cloud.Archived);
        Assert.Null(f.Services.GetRequiredService<ControllerManager>().CurrentId);
        Assert.Empty(f.Cloud.Created);
    }

    [Fact]
    public async Task Controller_Recreate_RouteRemoved()
    {
        using var f = new FyrdFactory();
        var r = await f.CreateClient().PostAsync("/api/hub/controller/recreate", null);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Empty(f.Cloud.Created);
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

    // ---- spend limits ----

    private static HubCostView Cost(decimal spent, decimal limit = 50, string? blocked = null, bool exempt = false) =>
        new(spent, limit, (double)(spent / limit), false, blocked, !exempt && spent >= limit * 0.9m, exempt);

    private static void SeedCost(FyrdFactory f, HubCostView cost, string id = SessionId) =>
        f.Services.GetRequiredService<HubState>().Publish([View(id) with { Cost = cost }], [],
            budget: new HubBudgetView("2026-11-01", "UTC", cost.CostToday, 500, false));

    [Fact]
    public async Task State_CarriesBudgetAndSessionCost()
    {
        using var f = new FyrdFactory();
        SeedCost(f, Cost(12.34m));
        var body = await ReadJson(await f.CreateClient().GetAsync("/api/hub/state"));
        var budget = body.GetProperty("budget");
        Assert.Equal("2026-11-01", budget.GetProperty("date").GetString());
        Assert.Equal("UTC", budget.GetProperty("timeZone").GetString());
        Assert.Equal(12.34m, budget.GetProperty("fyrdCostToday").GetDecimal());
        Assert.Equal(500m, budget.GetProperty("fyrdLimitUsd").GetDecimal());
        Assert.False(budget.GetProperty("fyrdBlocked").GetBoolean());
        var cost = body.GetProperty("sessions")[0].GetProperty("cost");
        Assert.Equal(12.34m, cost.GetProperty("costToday").GetDecimal());
        Assert.Equal(50m, cost.GetProperty("limitUsd").GetDecimal());
        Assert.Equal(JsonValueKind.Null, cost.GetProperty("blocked").ValueKind);
        Assert.False(cost.GetProperty("canExtend").GetBoolean());
        Assert.False(cost.GetProperty("exempt").GetBoolean());
        Assert.False(cost.GetProperty("estimated").GetBoolean());
    }

    [Fact]
    public async Task State_Empty_BudgetNull()
    {
        using var f = new FyrdFactory();
        var body = await ReadJson(await f.CreateClient().GetAsync("/api/hub/state"));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("budget").ValueKind);
    }

    [Theory]
    [InlineData("session", "This session reached its daily spend limit.")]
    [InlineData("fyrd", "Fyrd reached its daily spend limit.")]
    public async Task MessageAndResume_Blocked_409(string blocked, string detail)
    {
        using var f = new FyrdFactory();
        SeedCost(f, Cost(60, blocked: blocked));
        var c = f.CreateClient();
        var m = await c.PostAsJsonAsync($"/api/hub/sessions/{SessionId}/message", new { text = "hi" });
        Assert.Equal(HttpStatusCode.Conflict, m.StatusCode);
        Assert.Equal(detail, (await ReadJson(m)).GetProperty("detail").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync($"/api/hub/sessions/{SessionId}/resume", null)).StatusCode);
        Assert.Empty(f.Cloud.Sent);
        // Pause stays available so the owner can still stop a running session.
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsync($"/api/hub/sessions/{SessionId}/pause", null)).StatusCode);
    }

    [Fact]
    public async Task MessageAndResume_Controller_UnblockedUnderFyrdBlock()
    {
        using var f = new FyrdFactory();
        SeedCost(f, Cost(80, exempt: true), "session_ctl1"); // the guard never blocks the Controller
        var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.NoContent,
            (await c.PostAsJsonAsync("/api/hub/sessions/session_ctl1/message", new { text = "hi" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsync("/api/hub/sessions/session_ctl1/resume", null)).StatusCode);
    }

    [Theory] [InlineData(5)] [InlineData(50)] [InlineData(0)]
    public async Task Budget_BadAmount_400(int amount)
    {
        using var f = new FyrdFactory();
        SeedCost(f, Cost(46));
        var r = await f.CreateClient().PostAsJsonAsync($"/api/hub/sessions/{SessionId}/budget", new { amountUsd = amount });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Budget_MissingBody_400()
    {
        using var f = new FyrdFactory();
        SeedCost(f, Cost(46));
        var r = await f.CreateClient().PostAsync($"/api/hub/sessions/{SessionId}/budget",
            new StringContent("", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Budget_UnknownSession_404()
    {
        using var f = new FyrdFactory();
        var r = await f.CreateClient().PostAsJsonAsync("/api/hub/sessions/session_nope/budget", new { amountUsd = 25 });
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Budget_BelowNinetyPercent_409()
    {
        using var f = new FyrdFactory();
        SeedCost(f, Cost(44));
        var r = await f.CreateClient().PostAsJsonAsync($"/api/hub/sessions/{SessionId}/budget", new { amountUsd = 25 });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal(0m, f.Services.GetRequiredService<CostLedger>().ExtraUsd(SessionId));
    }

    [Fact]
    public async Task Budget_Controller_409()
    {
        using var f = new FyrdFactory();
        SeedCost(f, Cost(60, exempt: true), "session_ctl1");
        var r = await f.CreateClient().PostAsJsonAsync("/api/hub/sessions/session_ctl1/budget", new { amountUsd = 25 });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
    }

    [Theory] [InlineData(25, 75)] [InlineData(100, 150)]
    public async Task Budget_AtNinetyPercent_RaisesLimit_AndRequestsPoll(int amount, int newLimit)
    {
        using var f = new FyrdFactory();
        // The endpoint raises the ledger; the cost it reports comes from the ledger's own figure for the session.
        var ledger = f.Services.GetRequiredService<CostLedger>();
        ledger.Record(SessionId, "builder-abc", 45);
        SeedCost(f, Cost(45));
        var state = f.Services.GetRequiredService<HubState>();
        Assert.False(state.PollRequested.IsCompleted);

        var r = await f.CreateClient().PostAsJsonAsync($"/api/hub/sessions/{SessionId}/budget", new { amountUsd = amount });

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await ReadJson(r);
        Assert.Equal(newLimit, body.GetProperty("limitUsd").GetDecimal());
        Assert.Equal(45m, body.GetProperty("costToday").GetDecimal());
        Assert.True(state.PollRequested.IsCompleted);
        Assert.Equal(amount, ledger.ExtraUsd(SessionId));
    }
}

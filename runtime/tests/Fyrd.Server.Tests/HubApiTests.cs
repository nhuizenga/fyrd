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

    private static async Task<HttpResponseMessage> CreateController(FyrdFactory f, HttpClient c, string id = "session_ctl1")
    {
        f.Cloud.SetSession(Core.Tests.FakeCloudSessions.Session(id));
        f.Owner.CreatedIds.Enqueue(id);
        return await c.PostAsync("/api/hub/controller/create", null);
    }

    [Fact]
    public async Task State_WithController_ShowsIdAndUrl()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        await CreateController(f, c);
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
        await CreateController(f, c);
        // A poll after the create did not list the stored Controller (archived elsewhere): no dead link.
        var state = f.Services.GetRequiredService<HubState>();
        state.Publish([View()], []);
        state.LastPollAt = DateTimeOffset.UtcNow.AddMinutes(1);
        var body = await ReadJson(await c.GetAsync("/api/hub/state"));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("controller").ValueKind);
    }

    [Fact]
    public async Task State_JustCreated_ShowsControllerBeforeAnyPoll()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        await CreateController(f, c);
        var ctl = (await ReadJson(await c.GetAsync("/api/hub/state"))).GetProperty("controller");
        Assert.Equal("session_ctl1", ctl.GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task State_CreatedAfterLastPoll_ShowsController()
    {
        using var f = new FyrdFactory();
        var state = f.Services.GetRequiredService<HubState>();
        state.Publish([View()], []);
        state.LastPollAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var c = f.CreateClient();
        await CreateController(f, c);
        var ctl = (await ReadJson(await c.GetAsync("/api/hub/state"))).GetProperty("controller");
        Assert.Equal("session_ctl1", ctl.GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task Create_RequestsAnImmediatePoll()
    {
        using var f = new FyrdFactory();
        var state = f.Services.GetRequiredService<HubState>();
        Assert.False(state.PollRequested.IsCompleted);
        await CreateController(f, f.CreateClient());
        Assert.True(state.PollRequested.IsCompleted);
    }

    [Fact]
    public async Task Create_Ok_CreatesSessionWithPromptAndReturnsInfo()
    {
        using var f = new FyrdFactory();
        SetEnvironment(f);
        var r = await CreateController(f, f.CreateClient());
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await ReadJson(r);
        Assert.Equal("session_ctl1", body.GetProperty("sessionId").GetString());
        Assert.Equal("https://claude.ai/code/session_ctl1", body.GetProperty("url").GetString());
        var created = Assert.Single(f.Owner.Created);
        Assert.Equal("session_ctl1", created.SessionId);
        Assert.Equal("controller", created.Description);
        var sent = Assert.Single(f.Owner.Sent);
        Assert.Equal("session_ctl1", sent.SessionId);
        Assert.StartsWith("You are the Fyrd Controller.", sent.Text);
        Assert.Contains("environment_id: env_test1", sent.Text);
        Assert.Empty(f.Cloud.Created);
        Assert.Equal("session_ctl1", f.Services.GetRequiredService<ControllerManager>().CurrentId);
    }

    [Fact]
    public async Task Create_CliFails_502()
    {
        using var f = new FyrdFactory();
        f.Owner.Fail = new Core.CloudSessions.OwnerMessageException("The claude CLI is not installed or not on PATH.");
        var r = await f.CreateClient().PostAsync("/api/hub/controller/create", null);
        Assert.Equal(HttpStatusCode.BadGateway, r.StatusCode);
        Assert.Equal("The claude CLI is not installed or not on PATH.", (await ReadJson(r)).GetProperty("detail").GetString());
        Assert.Null(f.Services.GetRequiredService<ControllerManager>().CurrentId);
    }

    [Fact]
    public async Task Create_WhileAnotherRuns_409()
    {
        using var f = new FyrdFactory();
        var hold = new TaskCompletionSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cliCalls = 0;
        f.Owner.OnCreate = _ => { Interlocked.Increment(ref cliCalls); entered.TrySetResult(); return hold.Task; };
        var c = f.CreateClient();
        var first = c.PostAsync("/api/hub/controller/create", null);
        // Probe only once the first create is inside the CLI call: a probe sent earlier could reach the create gate first
        // and then wait on the held CLI call itself, since the endpoint passes no cancellation token.
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var probe = await c.PostAsync("/api/hub/controller/create", null).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.Conflict, probe.StatusCode);
        Assert.Equal("A Controller is already being created.", (await ReadJson(probe)).GetProperty("detail").GetString());
        hold.SetResult();
        Assert.Equal(HttpStatusCode.OK, (await first.WaitAsync(TimeSpan.FromSeconds(10))).StatusCode);
        Assert.Equal(1, cliCalls);
        Assert.Single(f.Owner.Created);
    }

    [Fact]
    public async Task Create_PreviousArchiveFails_200WithWarning()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await CreateController(f, c, "session_old")).StatusCode);
        f.Cloud.ThrowOnGetFor("session_old", new Core.CloudSessions.CloudSessionApiException(500, "boom"));

        var r = await CreateController(f, c, "session_new");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await ReadJson(r);
        Assert.Equal("session_new", body.GetProperty("sessionId").GetString());
        Assert.Contains("session_old", body.GetProperty("warning").GetString());
        Assert.Equal("session_new", f.Services.GetRequiredService<ControllerManager>().CurrentId);
    }

    [Fact]
    public async Task Adopt_EndpointRemoved_404()
    {
        using var f = new FyrdFactory();
        var r = await f.CreateClient().PostAsJsonAsync("/api/hub/controller/adopt", new { sessionId = "session_ctl1" });
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Empty(f.Owner.Sent);
    }

    [Fact]
    public async Task Controller_Find_Live_200()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        await CreateController(f, c);
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
        Assert.Equal("No Controller yet. Create one from the Sessions screen.",
            (await ReadJson(r)).GetProperty("detail").GetString());
        Assert.Empty(f.Cloud.Created);
    }

    [Fact]
    public async Task Controller_Find_Archived_404AndCleared()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        await CreateController(f, c);
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
        await CreateController(f, c);
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
        Assert.Empty(f.Owner.Sent);
    }

    [Fact]
    public async Task Message_HubSession_SendsText()
    {
        using var f = new FyrdFactory();
        Seed(f);
        var r = await f.CreateClient().PostAsJsonAsync($"/api/hub/sessions/{SessionId}/message", new { text = "hello" });
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        // The owner's own CLI sends composer turns, so the session reads them as the owner's, not a peer's.
        Assert.Equal((SessionId, "hello"), Assert.Single(f.Owner.Sent));
    }

    private static HubNotification Notification(string sessionId = SessionId) =>
        new($"{sessionId}:r1", sessionId, "builder-abc", "Which handle owns titan-lens?", "Still open: who owns it?",
            DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task State_ListsNotifications()
    {
        using var f = new FyrdFactory();
        Seed(f);
        f.Services.GetRequiredService<HubState>().PublishNotifications([Notification()]);

        var n = (await ReadJson(await f.CreateClient().GetAsync("/api/hub/state"))).GetProperty("notifications")[0];

        Assert.Equal($"{SessionId}:r1", n.GetProperty("id").GetString());
        Assert.Equal("Which handle owns titan-lens?", n.GetProperty("headline").GetString());
        Assert.Equal("Still open: who owns it?", n.GetProperty("text").GetString());
    }

    [Fact]
    public async Task DismissNotification_RemovesAndRemembers()
    {
        using var f = new FyrdFactory();
        Seed(f);
        var state = f.Services.GetRequiredService<HubState>();
        state.PublishNotifications([Notification()]);

        var r = await f.CreateClient().PostAsync($"/api/hub/notifications/{Uri.EscapeDataString($"{SessionId}:r1")}/dismiss", null);

        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Empty(state.Notifications);
        Assert.True(f.Services.GetRequiredService<NotificationDismissals>().Contains($"{SessionId}:r1"));
    }

    [Fact]
    public async Task DismissNotification_NotShowing_404()
    {
        using var f = new FyrdFactory();
        var r = await f.CreateClient().PostAsync("/api/hub/notifications/nope/dismiss", null);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Message_ClearsThatSessionsNotification()
    {
        using var f = new FyrdFactory();
        Seed(f);
        var state = f.Services.GetRequiredService<HubState>();
        state.PublishNotifications([Notification(), Notification("session_other")]);

        var r = await f.CreateClient().PostAsJsonAsync($"/api/hub/sessions/{SessionId}/message", new { text = "@servicetitan/lens" });

        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal(["session_other:r1"], state.Notifications.Select(n => n.Id));
        Assert.True(f.Services.GetRequiredService<NotificationDismissals>().Contains($"{SessionId}:r1"));
    }

    [Fact]
    public async Task Message_SendFails_KeepsNotification()
    {
        using var f = new FyrdFactory();
        Seed(f);
        var state = f.Services.GetRequiredService<HubState>();
        state.PublishNotifications([Notification()]);
        f.Owner.Fail = new OwnerMessageException("nope");

        await f.CreateClient().PostAsJsonAsync($"/api/hub/sessions/{SessionId}/message", new { text = "hi" });

        Assert.Single(state.Notifications);
    }

    [Fact]
    public async Task Message_CliFails_502WithItsError()
    {
        using var f = new FyrdFactory();
        Seed(f);
        f.Owner.Fail = new OwnerMessageException("cloud session x is archived and cannot accept new messages");
        var r = await f.CreateClient().PostAsJsonAsync($"/api/hub/sessions/{SessionId}/message", new { text = "hello" });
        Assert.Equal(HttpStatusCode.BadGateway, r.StatusCode);
        Assert.Equal("cloud session x is archived and cannot accept new messages",
            (await ReadJson(r)).GetProperty("detail").GetString());
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
        Assert.Empty(f.Owner.Sent);
    }

    [Fact]
    public async Task Resume_OwnerSendFails_Is502()
    {
        using var f = new FyrdFactory();
        Seed(f);
        f.Owner.Fail = new OwnerMessageException("claude is not on PATH");
        var r = await f.CreateClient().PostAsync($"/api/hub/sessions/{SessionId}/resume", null);
        Assert.Equal(HttpStatusCode.BadGateway, r.StatusCode);
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
        Assert.Equal((SessionId, "Continue where you left off."), Assert.Single(f.Owner.Sent));
    }

    [Fact]
    public async Task Archive_HubSession_ArchivesIt_204()
    {
        using var f = new FyrdFactory();
        Seed(f);
        var r = await f.CreateClient().PostAsync($"/api/hub/sessions/{SessionId}/archive", null);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal([SessionId], f.Cloud.Archived);
    }

    [Fact]
    public async Task Archive_NotHubSession_404_NothingArchived()
    {
        using var f = new FyrdFactory();
        var r = await f.CreateClient().PostAsync("/api/hub/sessions/session_other/archive", null);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Empty(f.Cloud.Archived);
    }

    [Fact]
    public async Task Rename_HubSession_RenamesIt_204()
    {
        using var f = new FyrdFactory();
        Seed(f);
        var r = await f.CreateClient().PostAsJsonAsync($"/api/hub/sessions/{SessionId}/rename", new { title = "  New name " });
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal((SessionId, "New name"), Assert.Single(f.Cloud.Renamed));
    }

    [Fact]
    public async Task Rename_BlankTitle_400_NotHubSession_404()
    {
        using var f = new FyrdFactory();
        Seed(f);
        var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync($"/api/hub/sessions/{SessionId}/rename", new { title = "  " })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/api/hub/sessions/session_other/rename", new { title = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync($"/api/hub/sessions/{SessionId}/rename", new { title = new string('x', 201) })).StatusCode);
        Assert.Empty(f.Cloud.Renamed);
    }

    [Fact]
    public async Task Rename_Controller_409()
    {
        using var f = new FyrdFactory();
        f.Services.GetRequiredService<HubState>().Publish([View() with { Role = "controller" }], []);
        var r = await f.CreateClient().PostAsJsonAsync($"/api/hub/sessions/{SessionId}/rename", new { title = "New" });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Empty(f.Cloud.Renamed);
    }

    private const string Pr = "https://github.com/acme/widgets/pull/12";
    private const string OtherPr = "https://github.com/acme/widgets/pull/99";

    private static void SeedWithPr(FyrdFactory f, bool draft, string checks = "passing", string state = "open", string mergeable = "mergeable")
    {
        f.Services.GetRequiredService<HubState>().Publish([View() with { PullRequests = [Pr] }], []);
        f.GitHub.Statuses[Pr] = new PullRequestStatus(Pr, 12, "Fix it", state, draft, checks) { Mergeable = mergeable };
    }

    [Fact]
    public async Task State_SessionsCarryTheirBranchPrs_FromTheWiredPoll()
    {
        using var f = new FyrdFactory();
        var raw = System.Text.Json.JsonDocument.Parse("""
            {"session_context":{"sources":[{"type":"git_repository","url":"https://github.com/acme/widgets"}],"outcomes":[]},
             "external_metadata":{"current_branches":{"":"fyrd/fix"}}}
            """).RootElement.Clone();
        f.Cloud.SetByTag("fyrd-hub", new CloudSession(SessionId, "builder-1", "idle", "connected", null, ["fyrd-hub", "fyrd-role-builder"], raw));
        f.Cloud.AddAssistantEvent(SessionId, "e1", $"Opened {Pr}; reviewing {OtherPr}.");
        f.GitHub.Heads.Add(new PullRequestHead(Pr, "fyrd/fix"));

        await f.Services.GetRequiredService<HubRunner>().PollOnceAsync(default);

        var s = (await ReadJson(await f.CreateClient().GetAsync("/api/hub/state"))).GetProperty("sessions").EnumerateArray().Single();
        Assert.Equal("fyrd/fix", s.GetProperty("branch").GetString());
        Assert.Equal([Pr], s.GetProperty("pullRequests").EnumerateArray().Select(e => e.GetString()));
        Assert.False(s.TryGetProperty("mentionedPullRequests", out _));
    }

    [Fact]
    public async Task PullRequests_UnownedPr_CannotBeMerged()
    {
        using var f = new FyrdFactory();
        SeedWithPr(f, draft: false);
        var r = await f.CreateClient().PostAsJsonAsync($"/api/hub/sessions/{SessionId}/pull-requests/merge", new { url = OtherPr });
        Assert.NotEqual(HttpStatusCode.OK, r.StatusCode);
        Assert.Empty(f.GitHub.Merged);
    }

    [Fact]
    public async Task PullRequests_ListsStatusesOfTheSessionsPrs()
    {
        using var f = new FyrdFactory();
        SeedWithPr(f, draft: true);
        var r = await f.CreateClient().GetAsync($"/api/hub/sessions/{SessionId}/pull-requests");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var pr = (await ReadJson(r)).EnumerateArray().Single();
        Assert.Equal(Pr, pr.GetProperty("url").GetString());
        Assert.Equal(12, pr.GetProperty("number").GetInt32());
        Assert.Equal("open", pr.GetProperty("state").GetString());
        Assert.True(pr.GetProperty("draft").GetBoolean());
        Assert.Equal("passing", pr.GetProperty("checks").GetString());
        Assert.False(pr.GetProperty("canMerge").GetBoolean());
    }

    [Fact]
    public async Task PullRequests_ProjectsTheRichStatusFields()
    {
        using var f = new FyrdFactory();
        SeedWithPr(f, draft: false, checks: "failing");
        f.GitHub.Statuses[Pr] = new PullRequestStatus(Pr, 12, "Fix it", "open", false, "failing")
        {
            Base = "main", Head = "fyrd/a", ChecksPassed = 8, ChecksFailed = 1, ChecksPending = 2,
            FailingCheck = new FailingCheck("lint", "https://ci/lint"), Review = "commented",
            Mergeable = "conflicting", MergeState = "dirty",
        };
        var r = await f.CreateClient().GetAsync($"/api/hub/sessions/{SessionId}/pull-requests");
        var pr = (await ReadJson(r)).EnumerateArray().Single();
        Assert.Equal("main", pr.GetProperty("base").GetString());
        Assert.Equal("fyrd/a", pr.GetProperty("head").GetString());
        Assert.Equal(8, pr.GetProperty("checksPassed").GetInt32());
        Assert.Equal(1, pr.GetProperty("checksFailed").GetInt32());
        Assert.Equal(2, pr.GetProperty("checksPending").GetInt32());
        Assert.Equal("lint", pr.GetProperty("failingCheck").GetProperty("name").GetString());
        Assert.Equal("https://ci/lint", pr.GetProperty("failingCheck").GetProperty("url").GetString());
        Assert.Equal("commented", pr.GetProperty("review").GetString());
        Assert.Equal("conflicting", pr.GetProperty("mergeable").GetString());
        Assert.Equal("dirty", pr.GetProperty("mergeState").GetString());
        Assert.False(pr.GetProperty("canMerge").GetBoolean());
    }

    [Fact]
    public async Task Merge_ConflictingPr_409_MentionsConflicts_NothingMerged()
    {
        using var f = new FyrdFactory();
        SeedWithPr(f, draft: false, mergeable: "conflicting");
        var r = await f.CreateClient().PostAsJsonAsync($"/api/hub/sessions/{SessionId}/pull-requests/merge", new { url = Pr });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains("merge conflicts", await r.Content.ReadAsStringAsync());
        Assert.Empty(f.GitHub.Merged);
    }

    [Fact]
    public async Task PullRequests_GitHubFailure_ReportedPerPr_NotAnError()
    {
        using var f = new FyrdFactory();
        SeedWithPr(f, draft: false);
        f.GitHub.Fail = new GitHubException("gh is not signed in");
        var r = await f.CreateClient().GetAsync($"/api/hub/sessions/{SessionId}/pull-requests");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var pr = (await ReadJson(r)).EnumerateArray().Single();
        Assert.Equal("unknown", pr.GetProperty("state").GetString());
        Assert.Equal("gh is not signed in", pr.GetProperty("error").GetString());
    }

    [Fact]
    public async Task State_ReportsGitHubSignIn_WithoutAToken()
    {
        using var f = new FyrdFactory();
        f.GitHub.Configured = false;
        var gh = (await ReadJson(await f.CreateClient().GetAsync("/api/hub/state"))).GetProperty("gitHub");
        Assert.False(gh.GetProperty("configured").GetBoolean());
        Assert.Equal(JsonValueKind.Null, gh.GetProperty("login").ValueKind);
        Assert.Equal(JsonValueKind.Null, gh.GetProperty("error").ValueKind);
    }

    [Fact]
    public async Task State_ReportsTheLogin_AfterARefresh_AndAnError_WhenTheTokenIsBad()
    {
        using var f = new FyrdFactory();
        var signIn = f.Services.GetRequiredService<GitHubSignIn>();
        await signIn.RefreshAsync();
        var gh = (await ReadJson(await f.CreateClient().GetAsync("/api/hub/state"))).GetProperty("gitHub");
        Assert.True(gh.GetProperty("configured").GetBoolean());
        Assert.Equal("octo", gh.GetProperty("login").GetString());

        f.GitHub.Fail = new GitHubException("GitHub rejected the token (bad or expired).");
        await signIn.RefreshAsync();
        gh = (await ReadJson(await f.CreateClient().GetAsync("/api/hub/state"))).GetProperty("gitHub");
        Assert.Equal(JsonValueKind.Null, gh.GetProperty("login").ValueKind);
        Assert.Contains("bad or expired", gh.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("GET", "pull-requests")]
    [InlineData("POST", "pull-requests/ready")]
    [InlineData("POST", "pull-requests/merge")]
    public async Task PrEndpoints_WithoutAToken_503(string method, string path)
    {
        using var f = new FyrdFactory();
        SeedWithPr(f, draft: true);
        f.GitHub.Configured = false;
        var url = $"/api/hub/sessions/{SessionId}/{path}";
        var r = method == "GET" ? await f.CreateClient().GetAsync(url) : await f.CreateClient().PostAsJsonAsync(url, new { url = Pr });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        Assert.Contains("GitHub is not configured", await r.Content.ReadAsStringAsync());
        Assert.Empty(f.GitHub.Readied);
        Assert.Empty(f.GitHub.Merged);
    }

    [Fact]
    public async Task PrActions_AreAcceptedOnAStackedPr_ButNotOnAPrOutsideTheStack()
    {
        using var f = new FyrdFactory();
        const string Stacked = "https://github.com/acme/widgets/pull/13";
        f.Services.GetRequiredService<HubState>().Publish(
            [View() with { PullRequests = [Pr, Stacked], StackedPullRequests = [Stacked] }], []);
        f.GitHub.Statuses[Pr] = new PullRequestStatus(Pr, 12, "own", "open", false, "passing");
        f.GitHub.Statuses[Stacked] = new PullRequestStatus(Stacked, 13, "stacked", "open", true, "passing");
        f.GitHub.Statuses[OtherPr] = new PullRequestStatus(OtherPr, 99, "other", "open", false, "passing");

        var ready = await f.CreateClient().PostAsJsonAsync($"/api/hub/sessions/{SessionId}/pull-requests/ready", new { url = Stacked });
        Assert.Equal(HttpStatusCode.NoContent, ready.StatusCode);
        var merge = await f.CreateClient().PostAsJsonAsync($"/api/hub/sessions/{SessionId}/pull-requests/merge", new { url = Pr });
        Assert.Equal(HttpStatusCode.NoContent, merge.StatusCode);
        var outside = await f.CreateClient().PostAsJsonAsync($"/api/hub/sessions/{SessionId}/pull-requests/merge", new { url = OtherPr });
        Assert.Equal(HttpStatusCode.BadRequest, outside.StatusCode);
        Assert.Equal([Stacked], f.GitHub.Readied);
        Assert.Equal([Pr], f.GitHub.Merged);
    }

    [Fact]
    public async Task AnAction_InvalidatesTheRepoListing_AndRequestsAPoll()
    {
        using var f = new FyrdFactory();
        SeedWithPr(f, draft: false);
        f.GitHub.Heads.Add(new PullRequestHead(Pr, "fyrd/a") { State = "open", Base = "main" });
        var owned = f.Services.GetRequiredService<OwnedPullRequests>();
        var state = f.Services.GetRequiredService<HubState>();
        await owned.FindAsync("acme/widgets", ["fyrd/a"]);
        await owned.FindAsync("acme/widgets", ["fyrd/a"]);
        Assert.Equal(1, f.GitHub.Listings);
        state.ResetPollRequest();
        Assert.False(state.PollRequested.IsCompleted);

        var r = await f.CreateClient().PostAsJsonAsync($"/api/hub/sessions/{SessionId}/pull-requests/merge", new { url = Pr });
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);

        Assert.True(state.PollRequested.IsCompleted);
        await owned.FindAsync("acme/widgets", ["fyrd/a"]);
        Assert.Equal(2, f.GitHub.Listings);
    }

    [Fact]
    public async Task AFailedAction_DoesNotInvalidateOrPoll()
    {
        using var f = new FyrdFactory();
        SeedWithPr(f, draft: false, mergeable: "conflicting");
        var state = f.Services.GetRequiredService<HubState>();
        state.ResetPollRequest();
        var r = await f.CreateClient().PostAsJsonAsync($"/api/hub/sessions/{SessionId}/pull-requests/merge", new { url = Pr });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.False(state.PollRequested.IsCompleted);
    }

    [Fact]
    public async Task State_CarriesThePullRequestStatusMap_AndStackedUrls()
    {
        using var f = new FyrdFactory();
        var status = new PullRequestStatus(Pr, 12, "Fix it", "open", false, "pending") { Base = "main" };
        f.Services.GetRequiredService<HubState>().Publish(
            [View() with { PullRequests = [Pr], StackedPullRequests = [Pr] }], [], null, null,
            new Dictionary<string, PullRequestStatus> { [Pr] = status });
        var s = await ReadJson(await f.CreateClient().GetAsync("/api/hub/state"));
        Assert.Equal("pending", s.GetProperty("pullRequests").GetProperty(Pr).GetProperty("checks").GetString());
        Assert.Equal(Pr, s.GetProperty("sessions")[0].GetProperty("stackedPullRequests")[0].GetString());
    }

    private const string Child = "https://github.com/acme/widgets/pull/13";

    private static void SeedStack(FyrdFactory f)
    {
        var parent = new PullRequestStatus(Pr, 12, "parent", "open", false, "passing") { Head = "fyrd/a", Base = "main", Mergeable = "mergeable" };
        var child = new PullRequestStatus(Child, 13, "child", "open", false, "passing") { Head = "fyrd/b", Base = "fyrd/a", Mergeable = "mergeable" };
        f.Services.GetRequiredService<HubState>().Publish([View() with { PullRequests = [Pr, Child], StackedPullRequests = [Child] }], [], null, null,
            new Dictionary<string, PullRequestStatus> { [Pr] = parent, [Child] = child });
        f.GitHub.Statuses[Pr] = parent;
        f.GitHub.Statuses[Child] = child;
    }

    [Fact]
    public async Task Merge_ThatLeavesAStackedPrBehind_409_WithTheOrder_NothingMerged()
    {
        using var f = new FyrdFactory();
        SeedStack(f);
        var r = await f.CreateClient().PostAsJsonAsync($"/api/hub/sessions/{SessionId}/pull-requests/merge", new { url = Pr });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        var text = await r.Content.ReadAsStringAsync();
        Assert.Contains("#13 is stacked on this PR. Merge from the top of the stack down: merge #13, then this.", text);
        Assert.Empty(f.GitHub.Merged);
    }

    [Fact]
    public async Task Merge_WithForce_SkipsTheOrderGuard()
    {
        using var f = new FyrdFactory();
        SeedStack(f);
        var r = await f.CreateClient().PostAsJsonAsync($"/api/hub/sessions/{SessionId}/pull-requests/merge", new { url = Pr, force = true });
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal([Pr], f.GitHub.Merged);
    }

    [Fact]
    public async Task Merge_OfTheTopOfAStack_NeedsNoForce()
    {
        using var f = new FyrdFactory();
        SeedStack(f);
        var r = await f.CreateClient().PostAsJsonAsync($"/api/hub/sessions/{SessionId}/pull-requests/merge", new { url = Child });
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal([Child], f.GitHub.Merged);
    }

    [Fact]
    public async Task Merge_ForceDoesNotBypassOtherChecks()
    {
        using var f = new FyrdFactory();
        SeedWithPr(f, draft: true);
        var r = await f.CreateClient().PostAsJsonAsync($"/api/hub/sessions/{SessionId}/pull-requests/merge", new { url = Pr, force = true });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Empty(f.GitHub.Merged);
    }

    private const string Pr11 = "https://github.com/acme/widgets/pull/11";
    private const string Pr13 = "https://github.com/acme/widgets/pull/13";

    private static PullRequestStatus Open(string url, int n, string head, string @base, string checks = "passing") =>
        new(url, n, $"PR {n}", "open", false, checks) { Head = head, Base = @base, Mergeable = "mergeable" };

    private static void SeedChain(FyrdFactory f, params PullRequestStatus[] prs)
    {
        f.Services.GetRequiredService<HubState>().Publish([View() with { PullRequests = prs.Select(p => p.Url).ToList() }], []);
        foreach (var p in prs) f.GitHub.Statuses[p.Url] = p;
    }

    private static Task<HttpResponseMessage> MergeStack(FyrdFactory f, string url) =>
        f.CreateClient().PostAsJsonAsync($"/api/hub/sessions/{SessionId}/pull-requests/merge-stack", new { url });

    [Fact]
    public async Task MergeStack_StartsAJob_202_MergesTopDown_AndShowsInState()
    {
        using var f = new FyrdFactory();
        SeedChain(f, Open(Pr11, 11, "a", "main"), Open(Pr, 12, "b", "a"), Open(Pr13, 13, "c", "b"));

        var r = await MergeStack(f, Pr);

        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        var jobId = (await ReadJson(r)).GetProperty("jobId").GetString()!;
        var jobs = f.Services.GetRequiredService<StackMergeJobs>();
        await jobs.Completion(jobId)!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([Pr13, Pr, Pr11], f.GitHub.Merged);
        var job = (await ReadJson(await f.CreateClient().GetAsync("/api/hub/state"))).GetProperty("stackMerges").EnumerateArray().Single();
        Assert.Equal(jobId, job.GetProperty("id").GetString());
        Assert.Equal("done", job.GetProperty("status").GetString());
        Assert.Equal([Pr13, Pr, Pr11], job.GetProperty("prs").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task MergeStack_ASecondRequestOnARunningChain_409_AndCancelStopsIt()
    {
        using var f = new FyrdFactory();
        SeedChain(f, Open(Pr11, 11, "a", "main"), Open(Pr, 12, "b", "a"), Open(Pr13, 13, "c", "b"));
        // The top is pending: the job waits (the first pre-check allows pending below the top only, so make #12 pending instead).
        f.GitHub.Sequences[Pr] = new Queue<PullRequestStatus>([Open(Pr, 12, "b", "a", "pending")]);

        var first = await MergeStack(f, Pr13);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var jobId = (await ReadJson(first)).GetProperty("jobId").GetString()!;
        var second = await MergeStack(f, Pr11);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("already running", await second.Content.ReadAsStringAsync());

        var cancel = await f.CreateClient().PostAsync($"/api/hub/stack-merges/{jobId}/cancel", null);
        Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        var jobs = f.Services.GetRequiredService<StackMergeJobs>();
        await jobs.Completion(jobId)!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(StackMergeStatus.Cancelled, jobs.Snapshot().Single().Status);
    }

    private const string ForeignPr = "https://github.com/other/private/pull/5";
    private const string MissingPr = "https://github.com/acme/widgets/pull/404";

    /// <summary>The session's PRs: the given stack plus extra URLs (cached ones, say) that GitHub fails to read.</summary>
    private static void SeedChainWithUnreadable(FyrdFactory f, Dictionary<string, GitHubException> unreadable, params PullRequestStatus[] prs)
    {
        f.Services.GetRequiredService<HubState>().Publish(
            [View() with { PullRequests = [.. prs.Select(p => p.Url), .. unreadable.Keys] }], []);
        foreach (var p in prs) f.GitHub.Statuses[p.Url] = p;
        f.GitHub.OnGet = u => { if (unreadable.TryGetValue(u, out var e)) throw e; };
    }

    [Fact]
    public async Task MergeStack_AnUnreadablePrInAnotherRepo_OrOneThatDoesNotExist_DoesNotBlockTheStack()
    {
        using var f = new FyrdFactory();
        SeedChainWithUnreadable(f, new()
            {
                [ForeignPr] = new GitHubException("GitHub token lacks access to other/private."),
                [MissingPr] = new GitHubPullRequestNotFoundException(MissingPr),
            },
            Open(Pr11, 11, "a", "main"), Open(Pr, 12, "b", "a"));

        var r = await MergeStack(f, Pr);

        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        var jobId = (await ReadJson(r)).GetProperty("jobId").GetString()!;
        await f.Services.GetRequiredService<StackMergeJobs>().Completion(jobId)!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([Pr, Pr11], f.GitHub.Merged);
    }

    [Fact]
    public async Task MergeStack_APrInTheSameRepoThatFailsToRead_502_NothingMerged()
    {
        // It may be part of the stack (here it is the top), so merging the rest could split it.
        using var f = new FyrdFactory();
        SeedChainWithUnreadable(f, new() { [Pr13] = new GitHubException("GitHub answered 502 for acme/widgets.") },
            Open(Pr11, 11, "a", "main"), Open(Pr, 12, "b", "a"));

        var r = await MergeStack(f, Pr11);

        Assert.Equal(HttpStatusCode.BadGateway, r.StatusCode);
        Assert.Empty(f.Services.GetRequiredService<StackMergeJobs>().Snapshot());
        Assert.Empty(f.GitHub.Merged);
    }

    [Fact]
    public async Task MergeStack_TheRequestedPrDoesNotExist_502()
    {
        using var f = new FyrdFactory();
        SeedChainWithUnreadable(f, new() { [MissingPr] = new GitHubPullRequestNotFoundException(MissingPr) },
            Open(Pr11, 11, "a", "main"), Open(Pr, 12, "b", "a"));

        var r = await MergeStack(f, MissingPr);

        Assert.Equal(HttpStatusCode.BadGateway, r.StatusCode);
        Assert.Empty(f.GitHub.Merged);
    }

    [Fact]
    public async Task PullRequests_AnUnreadableCachedPr_IsListedWithItsError_TheOthersStillRead()
    {
        using var f = new FyrdFactory();
        SeedChainWithUnreadable(f, new() { [ForeignPr] = new GitHubException("GitHub token lacks access to other/private.") },
            Open(Pr11, 11, "a", "main"), Open(Pr, 12, "b", "a"));

        var r = await f.CreateClient().GetAsync($"/api/hub/sessions/{SessionId}/pull-requests");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var list = (await ReadJson(r)).EnumerateArray().ToList();
        Assert.Equal(3, list.Count);
        var foreign = list.Single(p => p.GetProperty("url").GetString() == ForeignPr);
        Assert.Equal("GitHub token lacks access to other/private.", foreign.GetProperty("error").GetString());
        Assert.Equal("open", list.Single(p => p.GetProperty("url").GetString() == Pr).GetProperty("state").GetString());
    }

    [Fact]
    public async Task Ready_OnAReadablePr_IsNotBlockedByAnUnreadableCachedOne()
    {
        using var f = new FyrdFactory();
        var draft = Open(Pr, 12, "b", "main") with { Draft = true };
        SeedChainWithUnreadable(f, new() { [ForeignPr] = new GitHubException("GitHub token lacks access to other/private.") }, draft);

        var r = await f.CreateClient().PostAsJsonAsync($"/api/hub/sessions/{SessionId}/pull-requests/ready", new { url = Pr });

        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal([Pr], f.GitHub.Readied);
    }

    [Fact]
    public async Task MergeStack_CancelOfAnUnknownJob_404()
    {
        using var f = new FyrdFactory();
        var r = await f.CreateClient().PostAsync("/api/hub/stack-merges/nope/cancel", null);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task MergeStack_NotStacked_409()
    {
        using var f = new FyrdFactory();
        SeedChain(f, Open(Pr, 12, "b", "main"));
        var r = await MergeStack(f, Pr);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Empty(f.GitHub.Merged);
    }

    [Fact]
    public async Task MergeStack_AFork_409()
    {
        using var f = new FyrdFactory();
        SeedChain(f, Open(Pr11, 11, "a", "main"), Open(Pr, 12, "b", "a"), Open(Pr13, 13, "c", "a"));
        var r = await MergeStack(f, Pr11);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains("forks", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task MergeStack_FailingChecksOrADraft_409_NothingMerged()
    {
        using var f = new FyrdFactory();
        SeedChain(f, Open(Pr11, 11, "a", "main", "failing"), Open(Pr, 12, "b", "a"));
        Assert.Equal(HttpStatusCode.Conflict, (await MergeStack(f, Pr)).StatusCode);
        SeedChain(f, Open(Pr11, 11, "a", "main"), Open(Pr, 12, "b", "a", "pending"));
        Assert.Equal(HttpStatusCode.Conflict, (await MergeStack(f, Pr)).StatusCode); // the top cannot merge yet
        Assert.Empty(f.GitHub.Merged);
    }

    [Fact]
    public async Task MergeStack_APrOutsideTheSession_400()
    {
        using var f = new FyrdFactory();
        SeedChain(f, Open(Pr11, 11, "a", "main"), Open(Pr, 12, "b", "a"));
        var r = await MergeStack(f, OtherPr);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task MergeStack_WithoutAToken_503()
    {
        using var f = new FyrdFactory();
        SeedChain(f, Open(Pr11, 11, "a", "main"), Open(Pr, 12, "b", "a"));
        f.GitHub.Configured = false;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await MergeStack(f, Pr)).StatusCode);
    }

    [Fact]
    public async Task PullRequests_NotHubSession_404()
    {
        using var f = new FyrdFactory();
        var r = await f.CreateClient().GetAsync("/api/hub/sessions/session_other/pull-requests");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Ready_MarksADraftPrReady_204()
    {
        using var f = new FyrdFactory();
        SeedWithPr(f, draft: true);
        var r = await f.CreateClient().PostAsJsonAsync($"/api/hub/sessions/{SessionId}/pull-requests/ready", new { url = Pr });
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal([Pr], f.GitHub.Readied);
    }

    [Fact]
    public async Task Ready_AlreadyReady_409_NothingDone()
    {
        using var f = new FyrdFactory();
        SeedWithPr(f, draft: false);
        var r = await f.CreateClient().PostAsJsonAsync($"/api/hub/sessions/{SessionId}/pull-requests/ready", new { url = Pr });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Empty(f.GitHub.Readied);
    }

    [Fact]
    public async Task Merge_ReadyGreenPr_Merges_204()
    {
        using var f = new FyrdFactory();
        SeedWithPr(f, draft: false);
        var r = await f.CreateClient().PostAsJsonAsync($"/api/hub/sessions/{SessionId}/pull-requests/merge", new { url = Pr });
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal([Pr], f.GitHub.Merged);
    }

    [Theory]
    [InlineData(true, "passing", "open")]
    [InlineData(false, "failing", "open")]
    [InlineData(false, "pending", "open")]
    [InlineData(false, "passing", "merged")]
    public async Task Merge_NotMergeable_409_NothingMerged(bool draft, string checks, string state)
    {
        using var f = new FyrdFactory();
        SeedWithPr(f, draft, checks, state);
        var r = await f.CreateClient().PostAsJsonAsync($"/api/hub/sessions/{SessionId}/pull-requests/merge", new { url = Pr });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Empty(f.GitHub.Merged);
    }

    [Theory]
    [InlineData("ready", OtherPr)]
    [InlineData("merge", OtherPr)]
    [InlineData("ready", "--repo=evil")]
    [InlineData("merge", "")]
    public async Task PrActions_OnlyForPrsOfThisSession_400(string action, string url)
    {
        using var f = new FyrdFactory();
        SeedWithPr(f, draft: true);
        var r = await f.CreateClient().PostAsJsonAsync($"/api/hub/sessions/{SessionId}/pull-requests/{action}", new { url });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Empty(f.GitHub.Readied);
        Assert.Empty(f.GitHub.Merged);
    }

    [Fact]
    public async Task PrActions_GitHubFailure_502()
    {
        using var f = new FyrdFactory();
        SeedWithPr(f, draft: true);
        f.GitHub.Fail = new GitHubException("gh failed");
        var r = await f.CreateClient().PostAsJsonAsync($"/api/hub/sessions/{SessionId}/pull-requests/ready", new { url = Pr });
        Assert.Equal(HttpStatusCode.BadGateway, r.StatusCode);
    }

    [Fact]
    public async Task Persona_PutModel_KeepsTheRepoText_AndResetRestoresTheDefault()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        var seed = await ReadJson(await c.GetAsync("/api/hub/personas/builder"));
        var defaultModel = seed.GetProperty("defaultModel").GetString()!;
        Assert.Equal(defaultModel, seed.GetProperty("model").GetString());
        var other = defaultModel == "claude-opus-5-5" ? "claude-haiku-5-5" : "claude-opus-5-5";

        // A definition or name in the body is ignored: only the model changes.
        var put = await c.PutAsJsonAsync("/api/hub/personas/builder", new { model = other, name = "Smith", definition = "Mine" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var got = await ReadJson(await c.GetAsync("/api/hub/personas/builder"));
        Assert.Equal(other, got.GetProperty("model").GetString());
        Assert.Equal(seed.GetProperty("name").GetString(), got.GetProperty("name").GetString());
        Assert.Equal(seed.GetProperty("definition").GetString(), got.GetProperty("definition").GetString());

        var reset = await c.PostAsync("/api/hub/personas/builder/reset", null);
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Equal(defaultModel, (await ReadJson(reset)).GetProperty("model").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsync("/api/hub/personas/nope/reset", null)).StatusCode);
    }

    [Fact]
    public async Task Persona_PutModel_Rejects()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/api/hub/personas/builder", new { model = "gpt-4" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/api/hub/personas/builder", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PutAsJsonAsync("/api/hub/personas/scout", new { model = "claude-opus-5-5" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/api/hub/personas/Bad_Id", new { model = "claude-opus-5-5" })).StatusCode);
    }

    [Fact]
    public async Task Persona_AddAndDelete_AreGone()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        var add = await c.PutAsJsonAsync("/api/hub/personas", new { name = "Scout", model = "claude-haiku-5-5", definition = "Look." });
        Assert.False(add.IsSuccessStatusCode);
        var del = await c.DeleteAsync("/api/hub/personas/builder");
        Assert.False(del.IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/hub/personas/builder")).StatusCode);
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
    public async Task PendingMemory_ListApproveEditReject_AndStateCount()
    {
        using var f = new FyrdFactory();
        var pending = f.Services.GetRequiredService<PendingMemory>();
        var now = DateTimeOffset.Parse("2026-11-01T00:00:00Z");
        var a = pending.Add("Use pnpm.", "builder-1", "s1", now)!;
        var b = pending.Add("Raw text.", "builder-2", "s2", now.AddMinutes(1))!;
        var c2 = pending.Add("Drop me.", "builder-3", "s3", now.AddMinutes(2))!;
        var c = f.CreateClient();

        Assert.Equal(3, (await ReadJson(await c.GetAsync("/api/hub/state"))).GetProperty("pendingMemory").GetInt32());
        var list = (await ReadJson(await c.GetAsync("/api/hub/memory/pending"))).EnumerateArray().ToList();
        Assert.Equal(["Drop me.", "Raw text.", "Use pnpm."], list.Select(e => e.GetProperty("text").GetString()).ToArray());

        var memory = f.Services.GetRequiredService<HubMemory>();
        var v0 = memory.Version;
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync($"/api/hub/memory/pending/{a.Id}/approve", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync($"/api/hub/memory/pending/{b.Id}/approve", new { text = "Edited." })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsync($"/api/hub/memory/pending/{c2.Id}/reject", null)).StatusCode);

        Assert.Equal(["Edited.", "Use pnpm."], memory.List(HubMemory.Shared).Select(e => e.Text).ToArray());
        Assert.True(memory.Version > v0);
        Assert.Equal(0, (await ReadJson(await c.GetAsync("/api/hub/state"))).GetProperty("pendingMemory").GetInt32());
    }

    [Fact]
    public async Task PendingMemory_BadRequests()
    {
        using var f = new FyrdFactory();
        var e = f.Services.GetRequiredService<PendingMemory>().Add("Keep.", "b", "s", DateTimeOffset.UtcNow)!;
        var c = f.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/api/hub/memory/pending/nope/approve", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsync("/api/hub/memory/pending/nope/reject", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await c.PostAsJsonAsync($"/api/hub/memory/pending/{e.Id}/approve", new { text = "  " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await c.PostAsJsonAsync($"/api/hub/memory/pending/{e.Id}/approve", new { text = new string('x', 2001) })).StatusCode);
        Assert.Equal(1, f.Services.GetRequiredService<PendingMemory>().Count);

        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/hub/memory/pending/{e.Id}/approve") { Content = JsonContent.Create(new { }) };
        req.Headers.Add("Origin", "http://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(req)).StatusCode);
        Assert.Equal(1, f.Services.GetRequiredService<PendingMemory>().Count);
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

    private static async Task<string> UploadAsync(HttpClient c, string name = "notes.txt", string content = "hello file", string mime = "text/plain")
    {
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(content));
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mime);
        form.Add(part, "file", name);
        var r = await c.PostAsync("/api/hub/uploads", form);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Upload_StoresFileAndReturnsItsId()
    {
        using var f = new FyrdFactory();
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent([1, 2, 3]), "file", "a.bin");
        var r = await f.CreateClient().PostAsync("/api/hub/uploads", form);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await r.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("file_1", j.GetProperty("id").GetString());
        Assert.Equal("a.bin", j.GetProperty("name").GetString());
        Assert.Equal(3, j.GetProperty("size").GetInt64());
        Assert.Equal(("a.bin", 3L), (f.Files.Uploads.Single().Name, f.Files.Uploads.Single().Length));
    }

    [Fact]
    public async Task Upload_BadRequests()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/hub/uploads", new { })).StatusCode);
        using var none = new MultipartFormDataContent { { new StringContent("x"), "other" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/api/hub/uploads", none)).StatusCode);
        using var empty = new MultipartFormDataContent { { new ByteArrayContent([]), "file", "e.txt" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/api/hub/uploads", empty)).StatusCode);
        Assert.Empty(f.Files.Uploads);
    }

    [Fact]
    public async Task Upload_Over25Mb_413_NothingUploaded()
    {
        using var f = new FyrdFactory();
        using var big = new MultipartFormDataContent { { new ByteArrayContent(new byte[25 * 1024 * 1024 + 1]), "file", "big.bin" } };
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await f.CreateClient().PostAsync("/api/hub/uploads", big)).StatusCode);
        Assert.Empty(f.Files.Uploads);
    }

    [Fact]
    public async Task Upload_FilesApiRejectsCredentials_502()
    {
        using var f = new FyrdFactory();
        f.Files.Fail = new CloudSessionAuthException(401);
        using var form = new MultipartFormDataContent { { new ByteArrayContent([1]), "file", "a.txt" } };
        Assert.Equal(HttpStatusCode.BadGateway, (await f.CreateClient().PostAsync("/api/hub/uploads", form)).StatusCode);
    }

    [Fact]
    public async Task Message_WithFiles_AppendsDownloadInstructions_AndConsumesIds()
    {
        using var f = new FyrdFactory();
        Seed(f);
        var c = f.CreateClient();
        var id = await UploadAsync(c);
        var r = await c.PostAsJsonAsync($"/api/hub/sessions/{SessionId}/message", new { text = "see attached", fileIds = new[] { id } });
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        var text = Assert.Single(f.Owner.Sent).Text;
        Assert.StartsWith("see attached\n\n", text);
        Assert.Contains("https://api.anthropic.com/v1/files/<file id>/content", text);
        Assert.Contains("anthropic-beta: oauth-2025-04-20", text);
        Assert.Contains($"\"notes.txt\" (text/plain, 10 bytes): file id {id}", text);
        Assert.DoesNotContain("Bearer", text);
        // The id was consumed: sending it again is refused.
        Assert.Equal(HttpStatusCode.BadRequest,
            (await c.PostAsJsonAsync($"/api/hub/sessions/{SessionId}/message", new { text = "again", fileIds = new[] { id } })).StatusCode);
    }

    [Fact]
    public async Task Message_OnlyFiles_NoText_Sends()
    {
        using var f = new FyrdFactory();
        Seed(f);
        var c = f.CreateClient();
        var id = await UploadAsync(c);
        Assert.Equal(HttpStatusCode.NoContent,
            (await c.PostAsJsonAsync($"/api/hub/sessions/{SessionId}/message", new { text = "", fileIds = new[] { id } })).StatusCode);
        Assert.StartsWith("The owner attached a file", Assert.Single(f.Owner.Sent).Text);
    }

    [Fact]
    public async Task Message_UnknownOrTooManyFileIds_400_NothingSent()
    {
        using var f = new FyrdFactory();
        Seed(f);
        var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await c.PostAsJsonAsync($"/api/hub/sessions/{SessionId}/message", new { text = "x", fileIds = new[] { "file_made_up" } })).StatusCode);
        var many = Enumerable.Range(0, 11).Select(i => $"file_{i}").ToArray();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await c.PostAsJsonAsync($"/api/hub/sessions/{SessionId}/message", new { text = "x", fileIds = many })).StatusCode);
        Assert.Empty(f.Owner.Sent);
    }

    [Fact]
    public async Task Answer_FreeTextWithFiles_AppendsDownloadInstructions()
    {
        using var f = new FyrdFactory();
        Seed(f, withQuestion: true);
        var c = f.CreateClient();
        var id = await UploadAsync(c, "photo.png", "png", "image/png");
        var r = await c.PostAsJsonAsync("/api/hub/questions/q1/answer", new { text = "None of those, see file", fileIds = new[] { id } });
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        var answer = Assert.Single(f.Cloud.Answered).Answers["Pick a fruit"];
        Assert.StartsWith("None of those, see file\n\n", answer);
        Assert.Contains($"\"photo.png\" (image/png, 3 bytes): file id {id}", answer);
    }

    [Fact]
    public async Task Upload_Over26MbBody_413_NothingUploaded()
    {
        using var f = new FyrdFactory();
        using var big = new MultipartFormDataContent { { new ByteArrayContent(new byte[30 * 1024 * 1024]), "file", "huge.bin" } };
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await f.CreateClient().PostAsync("/api/hub/uploads", big)).StatusCode);
        Assert.Empty(f.Files.Uploads);
    }

    [Fact]
    public async Task Upload_TooManyOutstandingFiles_429_NothingMoreUploaded()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        for (var i = 0; i < UploadStore.MaxOutstandingFiles; i++) await UploadAsync(c, $"f{i}.txt");
        using var form = new MultipartFormDataContent { { new ByteArrayContent([1]), "file", "one-more.txt" } };
        Assert.Equal(HttpStatusCode.TooManyRequests, (await c.PostAsync("/api/hub/uploads", form)).StatusCode);
        Assert.Equal(UploadStore.MaxOutstandingFiles, f.Files.Uploads.Count);
    }

    [Fact]
    public async Task Message_NullOrBlankFileIds_400_Not500()
    {
        using var f = new FyrdFactory();
        Seed(f);
        var c = f.CreateClient();
        foreach (var body in new[] { "{\"text\":\"x\",\"fileIds\":[null]}", "{\"text\":\"x\",\"fileIds\":[\"\"]}", "{\"text\":\"x\",\"fileIds\":[\"  \"]}" })
        {
            var r = await c.PostAsync($"/api/hub/sessions/{SessionId}/message", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        }
        Assert.Empty(f.Owner.Sent);
    }

    [Fact]
    public async Task Message_HugeFileIdArray_400_BeforeAnyLookup()
    {
        using var f = new FyrdFactory();
        Seed(f);
        var many = Enumerable.Repeat("file_1", 5000).ToArray();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await f.CreateClient().PostAsJsonAsync($"/api/hub/sessions/{SessionId}/message", new { text = "x", fileIds = many })).StatusCode);
    }

    [Fact]
    public async Task Message_SendFails_FilesAreHandedBack_AndRetrySucceeds()
    {
        using var f = new FyrdFactory();
        Seed(f);
        var c = f.CreateClient();
        var id = await UploadAsync(c);
        f.Owner.Fail = new Fyrd.Core.CloudSessions.OwnerMessageException("cli failed");
        var url = $"/api/hub/sessions/{SessionId}/message";
        Assert.Equal(HttpStatusCode.BadGateway, (await c.PostAsJsonAsync(url, new { text = "x", fileIds = new[] { id } })).StatusCode);
        f.Owner.Fail = null;
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsJsonAsync(url, new { text = "x", fileIds = new[] { id } })).StatusCode);
        Assert.Single(f.Owner.Sent);
    }

    [Fact]
    public async Task Message_ConcurrentSendsOfTheSameFile_OnlyOneWins()
    {
        using var f = new FyrdFactory();
        Seed(f);
        var c = f.CreateClient();
        var id = await UploadAsync(c);
        var url = $"/api/hub/sessions/{SessionId}/message";
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => c.PostAsJsonAsync(url, new { text = "x", fileIds = new[] { id } })));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.NoContent));
        Assert.Equal(5, results.Count(r => r.StatusCode == HttpStatusCode.BadRequest));
        Assert.Single(f.Owner.Sent);
    }

    [Fact]
    public async Task Message_TextPlusFileNotesTooLong_400()
    {
        using var f = new FyrdFactory();
        Seed(f);
        var c = f.CreateClient();
        var id = await UploadAsync(c);
        var r = await c.PostAsJsonAsync($"/api/hub/sessions/{SessionId}/message", new { text = new string('x', 19_900), fileIds = new[] { id } });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Empty(f.Owner.Sent);
    }

    [Fact]
    public async Task Answer_TwoQuestionRequest_FilesAreConsumedWhenRecorded_AndRestoredOnFailedSend()
    {
        using var f = new FyrdFactory();
        var state = f.Services.GetRequiredService<HubState>();
        state.Publish([View()], [Sibling("q1", "Pick a fruit"), Sibling("q2", "Pick a colour")]);
        var c = f.CreateClient();
        var first = await UploadAsync(c, "one.txt");
        var second = await UploadAsync(c, "two.txt");

        // The first answer is only recorded (202), but its file is already spent: it cannot be attached again.
        Assert.Equal(HttpStatusCode.Accepted,
            (await c.PostAsJsonAsync("/api/hub/questions/q1/answer", new { text = "see file", fileIds = new[] { first } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await c.PostAsJsonAsync("/api/hub/questions/q2/answer", new { text = "again", fileIds = new[] { first } })).StatusCode);

        // The final send fails: its own file is handed back so the owner can retry; the earlier answer stays recorded.
        f.Cloud.ThrowOnNextCall(new Fyrd.Core.CloudSessions.CloudSessionApiException(500, "boom"));
        Assert.Equal(HttpStatusCode.BadGateway,
            (await c.PostAsJsonAsync("/api/hub/questions/q2/answer", new { text = "blue", fileIds = new[] { second } })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await c.PostAsJsonAsync("/api/hub/questions/q2/answer", new { text = "blue", fileIds = new[] { second } })).StatusCode);
        Assert.Single(f.Cloud.Answered);
    }

    [Fact]
    public async Task Answer_ChoiceWithFiles_400()
    {
        using var f = new FyrdFactory();
        Seed(f, withQuestion: true);
        var c = f.CreateClient();
        var id = await UploadAsync(c);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await c.PostAsJsonAsync("/api/hub/questions/q1/answer", new { choice = "Banana", fileIds = new[] { id } })).StatusCode);
        Assert.Empty(f.Cloud.Answered);
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
        Assert.Empty(f.Owner.Sent);
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
        SpendOnLedger(f, 44);
        SeedCost(f, Cost(44));
        var r = await f.CreateClient().PostAsJsonAsync($"/api/hub/sessions/{SessionId}/budget", new { amountUsd = 25 });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal(0m, f.Services.GetRequiredService<CostLedger>().ExtraUsd(SessionId));
    }

    [Fact]
    public async Task Budget_Controller_409()
    {
        using var f = new FyrdFactory();
        await CreateController(f, f.CreateClient());
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
        SpendOnLedger(f, 45);
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

    [Fact]
    public async Task Budget_TwoSimultaneousRaises_OnlyOneApplies()
    {
        using var f = new FyrdFactory();
        var ledger = f.Services.GetRequiredService<CostLedger>();
        SpendOnLedger(f, 46);
        SeedCost(f, Cost(46));
        var c = f.CreateClient();
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            c.PostAsJsonAsync($"/api/hub/sessions/{SessionId}/budget", new { amountUsd = 100 })));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(3, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(100m, ledger.ExtraUsd(SessionId));
    }

    [Fact]
    public async Task Budget_NonNumericAmount_400()
    {
        using var f = new FyrdFactory();
        SeedCost(f, Cost(46));
        var r = await f.CreateClient().PostAsync($"/api/hub/sessions/{SessionId}/budget",
            new StringContent("{\"amountUsd\":\"lots\"}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Answer_BlockedSession_409()
    {
        using var f = new FyrdFactory();
        f.Services.GetRequiredService<HubState>().Publish([View() with { Cost = Cost(60, blocked: "session") }], [Question()]);
        var r = await f.CreateClient().PostAsJsonAsync("/api/hub/questions/q1/answer", new { choice = "Apple" });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Empty(f.Cloud.Answered);
    }

    [Fact]
    public async Task ConfigPut_OmittingSpendFields_KeepsStoredValues()
    {
        using var f = new FyrdFactory();
        var store = f.Services.GetRequiredService<ConfigStore>();
        store.Save(store.Load() with { SessionDailyLimitUsd = 12, FyrdDailyLimitUsd = 340, TimeZone = "UTC" });
        var r = await f.CreateClient().PutAsJsonAsync("/api/config", new { sessionCap = 3 });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var saved = store.Load();
        Assert.Equal(3, saved.SessionCap);
        Assert.Equal(12m, saved.SessionDailyLimitUsd);
        Assert.Equal(340m, saved.FyrdDailyLimitUsd);
        Assert.Equal("UTC", saved.TimeZone);
    }

    [Fact]
    public async Task ConfigPut_SendingSpendFields_Applies()
    {
        using var f = new FyrdFactory();
        var r = await f.CreateClient().PutAsJsonAsync("/api/config",
            new { sessionDailyLimitUsd = 20, fyrdDailyLimitUsd = 200, timeZone = "America/Los_Angeles" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var saved = f.Services.GetRequiredService<ConfigStore>().Load();
        Assert.Equal(20m, saved.SessionDailyLimitUsd);
        Assert.Equal("America/Los_Angeles", saved.TimeZone);
    }

    private static void SpendOnLedger(FyrdFactory f, decimal cost)
    {
        var raw = JsonDocument.Parse($"{{\"model_usage_since_last_result\":{{\"m\":{{\"costUSD\":{cost}}}}}}}").RootElement.Clone();
        f.Services.GetRequiredService<CostLedger>().Feed(SessionId,
            [new SessionEvent("r1", "result", DateTimeOffset.UtcNow, null, raw)]);
    }
}

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
        Assert.Empty(f.Cloud.Sent);
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
        Assert.Empty(f.Cloud.Sent);
        Assert.Empty(f.Owner.Sent);
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
    public async Task Persona_DeleteController_409()
    {
        using var f = new FyrdFactory();
        var r = await f.CreateClient().DeleteAsync("/api/hub/personas/controller");
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
    }

    [Fact]
    public async Task Persona_Reset_RestoresSeed_AndUnknownIs404()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        var seed = (await ReadJson(await c.GetAsync("/api/hub/personas/builder"))).GetProperty("definition").GetString();
        await c.PutAsJsonAsync("/api/hub/personas/builder",
            new { id = "builder", name = "Builder", model = "claude-sonnet-5-5", definition = "Mine", locked = false });
        Assert.Equal("Mine", (await ReadJson(await c.GetAsync("/api/hub/personas/builder"))).GetProperty("definition").GetString());
        var reset = await c.PostAsync("/api/hub/personas/builder/reset", null);
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        var body = await ReadJson(reset);
        Assert.Equal(seed, body.GetProperty("definition").GetString());
        Assert.False(body.GetProperty("updateAvailable").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsync("/api/hub/personas/nope/reset", null)).StatusCode);
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
        await Adopt(f, f.CreateClient());
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
        var r = await f.CreateClient().PutAsJsonAsync("/api/config", new { squadCap = 3 });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var saved = store.Load();
        Assert.Equal(3, saved.SquadCap);
        Assert.Equal(12m, saved.SessionDailyLimitUsd);
        Assert.Equal(340m, saved.FyrdDailyLimitUsd);
        Assert.Equal("UTC", saved.TimeZone);
    }

    [Fact]
    public async Task ConfigPut_SendingSpendFields_Applies()
    {
        using var f = new FyrdFactory();
        var r = await f.CreateClient().PutAsJsonAsync("/api/config",
            new { squadCap = 4, sessionDailyLimitUsd = 20, fyrdDailyLimitUsd = 200, timeZone = "America/Los_Angeles" });
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

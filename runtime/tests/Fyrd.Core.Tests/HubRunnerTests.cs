using System.Text.Json;
using Fyrd.Core.CloudSessions;
using Fyrd.Core.Config;
using Fyrd.Core.Hub;

namespace Fyrd.Core.Tests;

public sealed class HubRunnerTests : IDisposable
{
    private readonly TempHome _home = new();
    private readonly string _seeds = Path.Combine(Path.GetTempPath(), "fyrd-seeds-" + Guid.NewGuid());
    private readonly FakeCloudSessions _cloud = new();
    private readonly FixedTime _time = new(DateTimeOffset.Parse("2026-10-31T12:00:00Z"));
    private readonly HubMemory _memory;
    private readonly HubPersonas _personas;
    private readonly ControllerManager _controller;
    private readonly HubState _state = new();
    private readonly CostLedger _ledger;
    private readonly CostGuard _guard;

    public HubRunnerTests()
    {
        Directory.CreateDirectory(_seeds);
        File.WriteAllText(Path.Combine(_seeds, "controller.md"), "---\nname: Controller\nmodel: claude-opus-5-5\nlocked: true\n---\nControl things.");
        File.WriteAllText(Path.Combine(_seeds, "builder.md"), "---\nname: Builder\nmodel: claude-sonnet-5-5\n---\nBuild things.");
        _memory = new HubMemory(_home.Paths);
        _personas = new HubPersonas(_home.Paths, _seeds);
        var config = new ConfigStore(_home.Paths);
        config.Save(new FyrdConfig(EnvironmentId: "env_abc123"));
        _controller = new ControllerManager(_cloud, _personas, _memory, config, _home.Paths);
        _ledger = new CostLedger(_home.Paths, config, _time);
        _guard = new CostGuard(_ledger, config, _cloud, _controller);
    }

    public void Dispose()
    {
        _home.Dispose();
        if (Directory.Exists(_seeds)) Directory.Delete(_seeds, true);
    }

    private HubRunner Make() => new(_cloud, _memory, _personas, _controller, _state, _home.Paths, _time, _ledger, _guard);

    private static CloudSession Hub(string id, string title, string status = "idle", string? role = null,
        string raw = "{}")
    {
        var tags = new List<string> { "fyrd-hub" };
        if (role is not null) tags.Add("fyrd-role-" + role);
        return new CloudSession(id, title, status, "connected", null, tags, JsonDocument.Parse(raw).RootElement.Clone());
    }

    /// <summary>Adopts an untagged session the owner made on claude.ai, then forgets the prompt Fyrd sent it.</summary>
    private async Task Adopt(string id, string title, string status = "idle")
    {
        _cloud.SetSession(new CloudSession(id, title, status, "connected", null, [], FakeCloudSessions.Empty));
        await _controller.AdoptAsync(id, default);
        _cloud.Sent.Clear();
    }

    private static string Remember(string scope, string text) =>
        $"Noted.\n```fyrd-remember\nscope: {scope}\n{text}\n```\n";

    [Fact]
    public async Task Poll_RememberAppendedOnce()
    {
        _cloud.SetByTag("fyrd-hub", Hub("s1", "builder-ab12", role: "builder"));
        _cloud.AddAssistantEvent("s1", "e1", Remember("role", "Use pnpm, not npm."));

        await Make().PollOnceAsync(default);
        await Make().PollOnceAsync(default); // a fresh runner (restart) must not recapture either

        var entry = Assert.Single(_memory.List("builder"));
        Assert.Equal("Use pnpm, not npm.", entry.Text);
        Assert.Equal("builder-ab12", entry.Source);
        Assert.Empty(_memory.List(HubMemory.Shared));
    }

    [Fact]
    public async Task Poll_SharedScope_GoesToShared_AndUserEventsIgnored()
    {
        _cloud.SetByTag("fyrd-hub", Hub("s1", "builder-ab12", role: "builder"));
        _cloud.AddAssistantEvent("s1", "e1", Remember("shared", "Repo uses trunk-based dev."));
        _cloud.AddEvent("s1", new SessionEvent("e2", "user", DateTimeOffset.UnixEpoch, Remember("shared", "From a user."), FakeCloudSessions.Empty));

        var runner = Make();
        await runner.PollOnceAsync(default);
        await runner.PollOnceAsync(default);

        Assert.Equal("Repo uses trunk-based dev.", Assert.Single(_memory.List(HubMemory.Shared)).Text);
    }

    [Fact]
    public async Task Poll_NoRoleTag_RoleScopeSkipped()
    {
        _cloud.SetByTag("fyrd-hub", Hub("s1", "stray"));
        _cloud.AddAssistantEvent("s1", "e1", Remember("role", "Lost."));

        await Make().PollOnceAsync(default);

        Assert.Empty(_memory.All().Values.SelectMany(v => v));
    }

    [Fact]
    public async Task Poll_ArchivedThenRestored_NoRecapture()
    {
        _cloud.SetByTag("fyrd-hub", Hub("s1", "builder-ab12", role: "builder"));
        _cloud.AddAssistantEvent("s1", "e1", Remember("role", "Once only."));
        var runner = Make();
        await runner.PollOnceAsync(default);

        _cloud.SetByTag("fyrd-hub", Hub("s1", "builder-ab12", "archived", role: "builder"));
        await runner.PollOnceAsync(default);
        _cloud.SetByTag("fyrd-hub", Hub("s1", "builder-ab12", role: "builder"));
        await runner.PollOnceAsync(default);

        Assert.Single(_memory.List("builder"));
    }

    [Fact]
    public async Task Poll_EmptyListingThenBack_NoRecapture()
    {
        _cloud.SetByTag("fyrd-hub", Hub("s1", "builder-ab12", role: "builder"));
        _cloud.AddAssistantEvent("s1", "e1", Remember("role", "Once only."));
        var runner = Make();
        await runner.PollOnceAsync(default);

        _cloud.SetByTag("fyrd-hub");
        await runner.PollOnceAsync(default);
        _cloud.SetByTag("fyrd-hub", Hub("s1", "builder-ab12", role: "builder"));
        await runner.PollOnceAsync(default);

        Assert.Single(_memory.List("builder"));
    }

    [Fact]
    public async Task Poll_CaptureFailsMidBatch_EarlierEntriesNotReadded()
    {
        _cloud.SetByTag("fyrd-hub", Hub("s1", "builder-ab12", role: "builder"));
        _cloud.AddAssistantEvent("s1", "e1", Remember("shared", "First."));
        _cloud.AddAssistantEvent("s1", "e2", Remember("role", "Second."));
        // A directory where the builder scope file belongs makes that write fail.
        var blocker = Path.Combine(_home.Paths.HubMemoryDir, "builder.json");
        Directory.CreateDirectory(blocker);
        var runner = Make();

        await runner.PollOnceAsync(default);
        Assert.Contains("s1", _state.LastError);
        Directory.Delete(blocker);
        await runner.PollOnceAsync(default);

        Assert.Equal("First.", Assert.Single(_memory.List(HubMemory.Shared)).Text);
        Assert.Equal("Second.", Assert.Single(_memory.List("builder")).Text);
    }

    [Fact]
    public async Task Poll_ControllerRoleScope_GoesToShared()
    {
        await Adopt("session_ctl", "controller");
        _cloud.SetByTag("fyrd-hub", Hub("session_ctl", "controller", role: "builder"));
        _cloud.AddAssistantEvent("session_ctl", "e1", Remember("role", "Owner prefers small PRs."));

        await Make().PollOnceAsync(default);

        var entry = Assert.Single(_memory.List(HubMemory.Shared));
        Assert.Equal("Owner prefers small PRs.", entry.Text);
        Assert.Equal("controller", entry.Source);
        Assert.Empty(_memory.List("builder"));
        Assert.Equal("controller", Assert.Single(_state.Sessions).Role);
    }

    [Fact]
    public async Task Poll_AdoptedUntaggedController_PolledAsHubSession()
    {
        await Adopt("session_ctl", "My hub", "requires_action");
        _cloud.SetByTag("fyrd-hub", Hub("s1", "builder-ab12", role: "builder"));
        _cloud.AddAssistantEvent("session_ctl", "e1", Remember("role", "Owner prefers small PRs."));
        foreach (var e in FixtureEvents.Load("ask-user-pending.json")) _cloud.AddEvent("session_ctl", e);

        await Make().PollOnceAsync(default);

        var entry = Assert.Single(_memory.List(HubMemory.Shared));
        Assert.Equal("Owner prefers small PRs.", entry.Text);
        Assert.Equal("My hub", entry.Source);
        Assert.Empty(_memory.List("controller"));
        var view = _state.Sessions.Single(s => s.Id == "session_ctl");
        Assert.Equal("controller", view.Role);
        Assert.Equal("My hub", view.Title);
        Assert.Equal("requires_action", view.Status);
        Assert.Equal(2, _state.Sessions.Count);
        var q = Assert.Single(_state.Questions);
        Assert.Equal("session_ctl", q.SessionId);
        Assert.Equal("My hub", q.SessionTitle);
        Assert.Null(_state.LastError);
    }

    [Fact]
    public async Task Poll_AdoptedController_UntitledFallsBackToController()
    {
        await Adopt("session_ctl", "");
        _cloud.SetByTag("fyrd-hub");

        await Make().PollOnceAsync(default);

        Assert.Equal("controller", Assert.Single(_state.Sessions).Title);
    }

    [Fact]
    public async Task Poll_AdoptedController_ArchivedOrGone_NotListed()
    {
        await Adopt("session_ctl", "controller");
        _cloud.SetSession(new CloudSession("session_ctl", "controller", "archived", "connected", null, [], FakeCloudSessions.Empty));
        _cloud.SetByTag("fyrd-hub", Hub("s1", "builder-ab12", role: "builder"));

        await Make().PollOnceAsync(default);

        Assert.Equal("s1", Assert.Single(_state.Sessions).Id);
        Assert.Null(_state.LastError);
    }

    [Fact]
    public async Task Poll_AdoptedController_FetchFails_KeepsPreviousViewAndRecordsError()
    {
        await Adopt("session_ctl", "controller");
        _cloud.SetByTag("fyrd-hub", Hub("s1", "builder-ab12", role: "builder"));
        var runner = Make();
        await runner.PollOnceAsync(default);
        Assert.Equal(2, _state.Sessions.Count);

        _cloud.SkipCalls(1); // the tag listing succeeds
        _cloud.ThrowOnNextCall(new CloudSessionApiException(500, "boom")); // GetAsync on the Controller fails
        await runner.PollOnceAsync(default);

        Assert.Contains("session_ctl", _state.LastError);
        Assert.Equal(2, _state.Sessions.Count);
    }

    [Fact]
    public async Task Poll_AnsweredElsewhere_RemovesQuestion()
    {
        _cloud.SetByTag("fyrd-hub", Hub("q1", "builder-ab12", "requires_action", role: "builder"));
        var pending = FixtureEvents.Load("ask-user-pending.json");
        foreach (var e in pending) _cloud.AddEvent("q1", e);
        var runner = Make();

        await runner.PollOnceAsync(default);
        var q = Assert.Single(_state.Questions);
        Assert.Equal("q1:bda7e33e-50a0-41aa-bc1b-4cd5c14452da:0", q.Id);
        Assert.Equal("builder-ab12", q.SessionTitle);

        var seen = pending.Select(e => e.Uuid).ToHashSet();
        foreach (var e in FixtureEvents.Load("ask-user-answered.json").Where(e => !seen.Contains(e.Uuid)))
            _cloud.AddEvent("q1", e);
        await runner.PollOnceAsync(default);

        Assert.Empty(_state.Questions);
    }

    [Fact]
    public async Task Poll_SessionFails_KeepsAnswerClaim()
    {
        _cloud.SetByTag("fyrd-hub", Hub("q1", "builder-ab12", "requires_action", role: "builder"));
        foreach (var e in FixtureEvents.Load("ask-user-pending.json")) _cloud.AddEvent("q1", e);
        var runner = Make();
        await runner.PollOnceAsync(default);
        var q = Assert.Single(_state.Questions);
        Assert.True(_state.TryClaim(q.RequestId, q.SessionId));
        _state.CompleteRequest(q.RequestId);

        _cloud.SkipCalls(1); // the tag listing succeeds
        _cloud.ThrowOnNextCall(new CloudSessionApiException(500, "boom")); // reading q1's events fails
        await runner.PollOnceAsync(default);
        Assert.Empty(_state.Questions);

        await runner.PollOnceAsync(default); // the control_response is not visible yet: still pending in the cloud
        Assert.Empty(_state.Questions);
        Assert.False(_state.TryClaim(q.RequestId));
    }

    [Fact]
    public async Task Poll_PendingQuestion_FoundAfterRestart()
    {
        _cloud.SetByTag("fyrd-hub", Hub("q1", "builder-ab12", "requires_action", role: "builder"));
        foreach (var e in FixtureEvents.Load("ask-user-pending.json")) _cloud.AddEvent("q1", e);
        await Make().PollOnceAsync(default);

        await Make().PollOnceAsync(default);

        Assert.Single(_state.Questions);
    }

    [Fact]
    public async Task Poll_MemoryChanged_PostsUpdateToControllerOnce()
    {
        await Adopt("session_ctl", "controller");
        _cloud.SetByTag("fyrd-hub");
        var runner = Make();
        await runner.PollOnceAsync(default);
        Assert.Empty(_cloud.Sent);

        _memory.Add(HubMemory.Shared, "Ship on Fridays is fine.", "owner", _time.GetUtcNow());
        await runner.PollOnceAsync(default);
        await runner.PollOnceAsync(default);

        var sent = Assert.Single(_cloud.Sent);
        Assert.Equal("session_ctl", sent.SessionId);
        Assert.StartsWith("Fyrd memory update.", sent.Text);
        Assert.Contains("Ship on Fridays is fine.", sent.Text);
    }

    [Fact]
    public async Task Poll_AdoptDuringPass_NoMemoryPostToOldController()
    {
        await Adopt("session_old", "controller");
        _cloud.SetByTag("fyrd-hub");
        _memory.Add(HubMemory.Shared, "Ship on Fridays is fine.", "owner", _time.GetUtcNow());
        _cloud.SetSession(new CloudSession("session_new", "controller", "idle", "connected", null, [], FakeCloudSessions.Empty));
        string? adoptedHash = null;
        _cloud.BeforeNextListEvents = async () =>
        {
            await _controller.AdoptAsync("session_new", default);
            adoptedHash = _controller.PostedMemoryHash;
            _memory.Add(HubMemory.Shared, "Edited after the adoption.", "owner", _time.GetUtcNow());
        };

        await Make().PollOnceAsync(default);

        // Only the new Controller's adoption prompt was sent; the pass neither posted to the old one nor took its hash.
        var sent = Assert.Single(_cloud.Sent);
        Assert.Equal("session_new", sent.SessionId);
        Assert.Equal(adoptedHash, _controller.PostedMemoryHash);
    }

    [Fact]
    public async Task Poll_ControllerArchived_NoMemoryPost()
    {
        await Adopt("session_ctl", "controller");
        _cloud.SetSession(new CloudSession("session_ctl", "controller", "archived", "connected", null, [], FakeCloudSessions.Empty));
        _cloud.SetByTag("fyrd-hub");
        _memory.Add(HubMemory.Shared, "Ship on Fridays is fine.", "owner", _time.GetUtcNow());

        await Make().PollOnceAsync(default);

        Assert.Empty(_cloud.Sent);
    }

    [Fact]
    public async Task Poll_InterruptedQuestion_NotShown()
    {
        _cloud.SetByTag("fyrd-hub", Hub("q1", "builder-ab12", "idle", role: "builder"));
        foreach (var e in FixtureEvents.Load("ask-user-pending.json")) _cloud.AddEvent("q1", e);

        await Make().PollOnceAsync(default);

        Assert.Empty(_state.Questions);
    }

    [Fact]
    public async Task Poll_MemoryChanged_NoController_SendsNothing()
    {
        _cloud.SetByTag("fyrd-hub");
        _memory.Add(HubMemory.Shared, "x", "owner", _time.GetUtcNow());

        await Make().PollOnceAsync(default);

        Assert.Empty(_cloud.Sent);
    }

    [Fact]
    public async Task Poll_AuthError_SetsSignedOut()
    {
        _cloud.SetByTag("fyrd-hub", Hub("s1", "builder-ab12", role: "builder"));
        _cloud.ThrowOnNextCall(new CloudSessionAuthException(401));
        var runner = Make();

        await runner.PollOnceAsync(default);
        Assert.True(_state.SignedOut);
        Assert.Equal(_time.GetUtcNow(), _state.LastPollAt);

        await runner.PollOnceAsync(default);
        Assert.False(_state.SignedOut);
        Assert.Single(_state.Sessions);
    }

    [Fact]
    public async Task Poll_ClaudeAuthMissing_SetsSignedOut()
    {
        _cloud.SetByTag("fyrd-hub", Hub("s1", "builder-ab12", role: "builder"));
        _cloud.SkipCalls(1); // ListByTag succeeds; the events call fails
        _cloud.ThrowOnNextCall(new ClaudeAuthMissingException());

        await Make().PollOnceAsync(default);

        Assert.True(_state.SignedOut);
    }

    [Fact]
    public async Task Poll_SessionError_RecordedAndPassContinues()
    {
        _cloud.SetByTag("fyrd-hub", Hub("bad", "builder-1", role: "builder"), Hub("good", "builder-2", role: "builder"));
        _cloud.AddAssistantEvent("good", "e1", Remember("role", "Kept."));
        _cloud.SkipCalls(1);
        _cloud.ThrowOnNextCall(new CloudSessionApiException(500, "boom"));

        await Make().PollOnceAsync(default);

        Assert.False(_state.SignedOut);
        Assert.Contains("bad", _state.LastError);
        Assert.Single(_memory.List("builder"));
        Assert.Equal(2, _state.Sessions.Count);
    }

    [Fact]
    public async Task Poll_ArchivedSessionsSkipped()
    {
        _cloud.SetByTag("fyrd-hub",
            Hub("live", "builder-1", role: "builder"),
            Hub("old", "builder-2", "archived", role: "builder"));
        _cloud.AddAssistantEvent("old", "e1", Remember("role", "Stale."));

        await Make().PollOnceAsync(default);

        Assert.Equal("live", Assert.Single(_state.Sessions).Id);
        Assert.Empty(_memory.List("builder"));
        Assert.Single(_cloud.EventAfterIds);
    }

    [Fact]
    public async Task Poll_SessionView_ListsPullRequestsFromOutcomes()
    {
        const string raw = """
            {"session_context":{"outcomes":[{"type":"git_repository","git_info":{"repo":"acme/widgets","branches":["b"],
              "pull_requests":[{"url":"https://github.com/acme/widgets/pull/12"},"https://github.com/acme/widgets/pull/13"],
              "pr_url":"https://github.com/acme/widgets/pull/12"}}]}}
            """;
        _cloud.SetByTag("fyrd-hub", Hub("s1", "builder-ab12", "running", role: "builder", raw: raw), Hub("s2", "plain"));

        await Make().PollOnceAsync(default);

        Assert.Equal(["https://github.com/acme/widgets/pull/12", "https://github.com/acme/widgets/pull/13"],
            _state.Sessions.Single(s => s.Id == "s1").PullRequests);
        Assert.Empty(_state.Sessions.Single(s => s.Id == "s2").PullRequests);
    }

    [Fact]
    public async Task Poll_BuildsSessionView()
    {
        const string raw = """
            {"created_at":"2026-10-31T10:00:00Z",
             "session_context":{"sources":[{"type":"git_repository","url":"https://github.com/acme/widgets"}],
                                "outcomes":[{"type":"git_repository","git_info":{"type":"github","repo":"acme/widgets","branches":["claude/fix-thing"]}}]}}
            """;
        _cloud.SetByTag("fyrd-hub", Hub("s1", "builder-ab12", "running", role: "builder", raw: raw), Hub("s2", "plain", status: null!));
        _cloud.AddEvent("s1", new SessionEvent("e1", "assistant", DateTimeOffset.Parse("2026-10-31T11:00:00Z"), "hi", FakeCloudSessions.Empty));
        _cloud.AddEvent("s1", new SessionEvent("e2", "user", DateTimeOffset.Parse("2026-10-31T11:05:00Z"), "yo", FakeCloudSessions.Empty));

        await Make().PollOnceAsync(default);

        var v = _state.Sessions.Single(s => s.Id == "s1");
        Assert.Equal("builder-ab12", v.Title);
        Assert.Equal("builder", v.Role);
        Assert.Equal("running", v.Status);
        Assert.Equal("acme/widgets", v.Repo);
        Assert.Equal("claude/fix-thing", v.Branch);
        Assert.Equal(DateTimeOffset.Parse("2026-10-31T10:00:00Z"), v.CreatedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-10-31T11:05:00Z"), v.LastActivity);
        Assert.Contains("fyrd-role-builder", v.Tags);

        var p = _state.Sessions.Single(s => s.Id == "s2");
        Assert.Equal("unknown", p.Status);
        Assert.Null(p.Repo);
        Assert.Null(p.Branch);
        Assert.Null(p.CreatedAt);
        Assert.Null(p.LastActivity);
        Assert.Equal(_time.GetUtcNow(), _state.LastPollAt);
        Assert.Null(_state.LastError);
    }

    [Fact]
    public async Task Poll_AttachesCostAndBudget_FromResultEventsAndSessionUsage()
    {
        var turn = JsonDocument.Parse("{\"model_usage_since_last_result\":{\"m\":{\"costUSD\":2.0}}}").RootElement.Clone();
        _cloud.SetByTag("fyrd-hub", Hub("s1", "builder-ab12", role: "builder",
            raw: "{\"external_metadata\":{\"usage\":{\"cost_usd\":2.5}}}"));
        _cloud.AddEvent("s1", new SessionEvent("r1", "result", _time.GetUtcNow(), null, turn));

        await Make().PollOnceAsync(default);

        var v = Assert.Single(_state.Sessions);
        Assert.NotNull(v.Cost);
        Assert.Equal(2.5m, v.Cost!.CostToday);
        Assert.True(v.Cost.Estimated);
        Assert.Equal(50m, v.Cost.LimitUsd);
        Assert.Equal(2.5m, _state.Budget!.FyrdCostToday);
        Assert.Equal(500m, _state.Budget.FyrdLimitUsd);
    }

    [Fact]
    public async Task Poll_OverLimit_InterruptsOnce_AcrossPasses()
    {
        var turn = JsonDocument.Parse("{\"model_usage_since_last_result\":{\"m\":{\"costUSD\":75}}}").RootElement.Clone();
        _cloud.SetByTag("fyrd-hub", Hub("s1", "builder-ab12", role: "builder"));
        _cloud.AddEvent("s1", new SessionEvent("r1", "result", _time.GetUtcNow(), null, turn));
        var runner = Make();

        await runner.PollOnceAsync(default);
        await runner.PollOnceAsync(default);

        Assert.Equal(["s1"], _cloud.Interrupted);
        Assert.Equal("session", _state.Sessions[0].Cost!.Blocked);
    }

    [Fact]
    public async Task Poll_GuardFailure_ReportedInLastError_ViewsStillPublished()
    {
        var turn = JsonDocument.Parse("{\"model_usage_since_last_result\":{\"m\":{\"costUSD\":75}}}").RootElement.Clone();
        _cloud.SetByTag("fyrd-hub", Hub("s1", "builder-ab12", role: "builder"));
        _cloud.AddEvent("s1", new SessionEvent("r1", "result", _time.GetUtcNow(), null, turn));
        _cloud.SkipCalls(2); // list by tag, list events; then the interrupt fails
        _cloud.ThrowOnNextCall(new HttpRequestException("interrupt down"));

        await Make().PollOnceAsync(default);

        Assert.Single(_state.Sessions);
        Assert.Contains("interrupt down", _state.LastError);
    }
}

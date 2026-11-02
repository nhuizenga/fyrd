using Fyrd.Core.CloudSessions;
using Fyrd.Core.Orchestration;
using Fyrd.Core.Specs;

namespace Fyrd.Core.Tests;

public class SpecRunnerTests
{
    private const string F = "```";
    private static readonly DateTimeOffset T = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    private sealed class Rig : IDisposable
    {
        public TempHome Home = new();
        public FixedTime Time = new(T);
        public SpecStore Specs;
        public InboxStore Inbox;
        public FakeCloudSessions Cloud = new();
        public FakeOwnerMessages Owner = new();
        public RunnerState State = new();
        public SpecRunner Runner;

        public Rig()
        {
            Specs = new SpecStore(Home.Paths, Time);
            Inbox = new InboxStore(Home.Paths, Specs, new DecisionMemory(Home.Paths), Time);
            Runner = NewRunner();
        }

        public SpecRunner NewRunner() => new(Specs, Inbox, Cloud, State, Time, Relay);
        public LeadRelay Relay => new(Specs, Cloud, Owner, State, Inbox);

        public string NewSpec(string title = "Add export button", string lead = "lead1")
        {
            var id = Specs.Create(title, "owner/repo", "x").Id;
            Specs.SetLead(id, lead);
            Cloud.SetSession(FakeCloudSessions.Session(lead, "running"));
            return id;
        }

        public void Dispose() => Home.Dispose();
    }

    private static string Units(string json) => F + "fyrd-units\n" + json + "\n" + F;
    private static string Question(string json) => F + "fyrd-question\n" + json + "\n" + F;

    [Fact]
    public async Task Poll_UnitsBlock_WritesUnits()
    {
        using var r = new Rig();
        var id = r.NewSpec();
        r.Cloud.AddAssistantEvent("lead1", "e1", Units("[{\"id\":\"a\",\"title\":\"A\"},{\"id\":\"b\",\"title\":\"B\"}]"));
        await r.Runner.PollOnceAsync(default);
        var units = r.Specs.ListUnits(id);
        Assert.Equal(["a", "b"], units.Select(u => u.Id));
        Assert.All(units, u => Assert.Equal(UnitStage.Implement, u.Stage));
        Assert.Equal("e1", r.Specs.Get(id)!.LastEventId);
    }

    [Fact]
    public async Task Poll_QuestionBlock_CreatesCardWithSourceKey()
    {
        using var r = new Rig();
        var id = r.NewSpec();
        r.Cloud.AddAssistantEvent("lead1", "e1",
            Question("{\"id\":\"q-color\",\"question\":\"Which colour?\",\"options\":[\"red\",\"blue\"],\"unit\":\"a\"}"));
        await r.Runner.PollOnceAsync(default);
        var card = Assert.Single(r.Inbox.ListOpenFor(id));
        Assert.Equal(id + ":q-color", card.SourceKey);
        Assert.True(card.AllowFreeText);
        Assert.Equal("a", card.UnitId);
        Assert.Equal(["red", "blue"], card.Options);
    }

    [Fact]
    public async Task Poll_SameQuestionTwice_OneCard()
    {
        using var r = new Rig();
        var id = r.NewSpec();
        var q = Question("{\"id\":\"q-color\",\"question\":\"Which colour?\"}");
        r.Cloud.AddAssistantEvent("lead1", "e1", q);
        await r.Runner.PollOnceAsync(default);
        // The same question is asked again in a later event.
        r.Cloud.AddAssistantEvent("lead1", "e2", q);
        await r.Runner.PollOnceAsync(default);
        Assert.Single(r.Inbox.ListOpenFor(id));
    }

    [Fact]
    public async Task Poll_ResumesFromStoredCursor()
    {
        using var r = new Rig();
        r.NewSpec();
        r.Cloud.AddAssistantEvent("lead1", "e1", "hello");
        await r.Runner.PollOnceAsync(default);
        r.Cloud.EventAfterIds.Clear();
        await r.NewRunner().PollOnceAsync(default);
        Assert.Equal("e1", Assert.Single(r.Cloud.EventAfterIds));
    }

    [Fact]
    public async Task Poll_FollowsAllPages()
    {
        using var r = new Rig();
        var id = r.NewSpec();
        r.Cloud.PageSize = 1;
        r.Cloud.AddAssistantEvent("lead1", "e1", Units("[{\"id\":\"a\",\"title\":\"A\"}]"));
        r.Cloud.AddAssistantEvent("lead1", "e2", Question("{\"id\":\"q1\",\"question\":\"Why?\"}"));
        r.Cloud.AddAssistantEvent("lead1", "e3", "tail");
        await r.Runner.PollOnceAsync(default);
        Assert.Single(r.Specs.ListUnits(id));
        Assert.Single(r.Inbox.ListOpenFor(id));
        Assert.Equal("e3", r.Specs.Get(id)!.LastEventId);
    }

    [Fact]
    public async Task Poll_PeerSessions_SetStageAndState()
    {
        using var r = new Rig();
        var id = r.NewSpec();
        r.Cloud.AddAssistantEvent("lead1", "e1",
            Units("[{\"id\":\"a\",\"title\":\"A\"},{\"id\":\"b\",\"title\":\"B\"}]") + "\n" +
            Question("{\"id\":\"qb\",\"question\":\"Pick?\",\"unit\":\"b\"}"));
        r.Cloud.SetByTag("fyrd-spec-" + id,
            FakeCloudSessions.Session("p1", "idle", ["fyrd-role-implementer", "fyrd-unit-a"], T),
            FakeCloudSessions.Session("p2", "running", ["fyrd-role-implementer", "fyrd-unit-b"], T),
            FakeCloudSessions.Session("p3", "running", ["fyrd-role-reviewer", "fyrd-unit-b"], T.AddMinutes(5)));
        await r.Runner.PollOnceAsync(default);
        var units = r.Specs.ListUnits(id).ToDictionary(u => u.Id);
        Assert.Equal(UnitStage.Implement, units["a"].Stage);
        Assert.Equal(UnitState.Idle, units["a"].State);
        Assert.Equal(UnitStage.Review, units["b"].Stage);
        Assert.Equal(UnitState.WaitingOnOwner, units["b"].State);
        Assert.True(r.State.Sessions.ContainsKey("p3"));
        Assert.True(r.State.Sessions.ContainsKey("lead1"));
    }

    [Fact]
    public async Task Poll_ArchivedPeerIgnoredForStage()
    {
        using var r = new Rig();
        var id = r.NewSpec();
        r.Cloud.AddAssistantEvent("lead1", "e1", Units("[{\"id\":\"a\",\"title\":\"A\"}]"));
        r.Cloud.SetByTag("fyrd-spec-" + id,
            FakeCloudSessions.Session("p1", "running", ["fyrd-role-implementer", "fyrd-unit-a"], T),
            FakeCloudSessions.Session("p2", "archived", ["fyrd-role-reviewer", "fyrd-unit-a"], T.AddMinutes(5)));
        await r.Runner.PollOnceAsync(default);
        var u = Assert.Single(r.Specs.ListUnits(id));
        Assert.Equal(UnitStage.Implement, u.Stage);
        Assert.DoesNotContain("p2", r.State.Sessions.Keys);
    }

    [Fact]
    public async Task Poll_AttentionCardAfterAnsweredQuestion()
    {
        using var r = new Rig();
        var id = r.NewSpec();
        r.Cloud.AddAssistantEvent("lead1", "e1", Question("{\"id\":\"q1\",\"question\":\"Pick?\"}"));
        r.Cloud.SetSession(FakeCloudSessions.Session("lead1", "idle", needsAction: "Waiting for input"));
        await r.Runner.PollOnceAsync(default);
        var q = Assert.Single(r.Inbox.ListOpenFor(id));
        Assert.True(r.State.LastTurnHadQuestion(id));

        var relay = r.Relay;
        var answered = r.Inbox.Answer(q.Id, "blue");
        await relay.RelayAnswerAsync(answered, default);
        Assert.False(r.State.LastTurnHadQuestion(id));

        r.Cloud.SetSession(FakeCloudSessions.Session("lead1", "idle", needsAction: "Approve Bash: ls"));
        await r.Runner.PollOnceAsync(default);
        Assert.Contains(r.Inbox.ListOpenFor(id), c => c.Question == "Lead needs attention: Approve Bash: ls");
    }

    [Fact]
    public async Task Poll_RetriesPendingRelay_Once()
    {
        using var r = new Rig();
        var id = r.NewSpec();
        var q = r.Inbox.Add(id, null, "Pick?", [], null, null, null, true, id + ":q1");
        var answered = r.Inbox.Answer(q.Id, "blue");
        r.Inbox.SetRelay(answered.Id, "pending", null);

        await r.NewRunner().PollOnceAsync(default); // simulates a restarted runner
        await r.NewRunner().PollOnceAsync(default);

        Assert.Single(r.Owner.Sent);
        Assert.Equal("sent", r.Inbox.Get(q.Id).Answer!.RelayStatus);
    }

    [Fact]
    public async Task Poll_FailedRelay_RecordsFailureAndRetries()
    {
        using var r = new Rig();
        var id = r.NewSpec();
        var q = r.Inbox.Add(id, null, "Pick?", [], null, null, null, true, id + ":q1");
        var card = r.Inbox.SetRelay(r.Inbox.Answer(q.Id, "blue").Id, "pending", null);
        r.Owner.ThrowOnNextCall(new HttpRequestException("boom"));
        Assert.Equal("boom", await r.Relay.DeliverAsync(card, default));
        var failed = r.Inbox.Get(q.Id).Answer!;
        Assert.Equal("failed", failed.RelayStatus);
        Assert.Equal("boom", failed.RelayError);
        await r.Runner.PollOnceAsync(default);
        Assert.Equal("sent", r.Inbox.Get(q.Id).Answer!.RelayStatus);
    }

    [Fact]
    public async Task Poll_SpecErrors_StillRetriesRelayAndReconciles()
    {
        using var r = new Rig();
        var ok = r.NewSpec("Other spec", "leadok");
        var q = r.Inbox.Add(ok, null, "Pick?", [], null, null, null, true, ok + ":q1");
        r.Inbox.Answer(q.Id, "blue");
        var stale = r.Specs.Create("Stale one", "owner/repo", "x").Id;
        r.Specs.UpdateStatus(stale, SpecStatus.Queued);
        r.Time.Advance(TimeSpan.FromMinutes(3));
        r.Cloud.ThrowOnNextCall(new HttpRequestException("poll boom")); // the spec poll errors
        await r.Runner.PollOnceAsync(default);
        Assert.NotNull(r.State.LastError);
        Assert.Single(r.Owner.Sent);
        Assert.Equal(SpecStatus.Draft, r.Specs.Get(stale)!.Status);
    }

    [Fact]
    public async Task Poll_RelayAndLaunchErrors_AreJoined()
    {
        using var r = new Rig();
        var ok = r.NewSpec("Other spec", "leadok");
        var q = r.Inbox.Add(ok, null, "Pick?", [], null, null, null, true, ok + ":q1");
        r.Inbox.Answer(q.Id, "blue");
        r.Specs.UpdateStatus(r.Specs.Create("Stale one", "owner/repo", "x").Id, SpecStatus.Queued);
        r.Time.Advance(TimeSpan.FromMinutes(3));
        r.Cloud.SkipCalls(SkippedPollCalls); // the healthy spec poll
        r.Owner.ThrowOnNextCall(new HttpRequestException("relay boom"));
        r.Cloud.ThrowOnNextCall(new HttpRequestException("launch boom"));
        await r.Runner.PollOnceAsync(default);
        var error = r.State.LastError!;
        Assert.Contains("relay boom", error);
        Assert.Contains("launch boom", error);
    }

    private const int SkippedPollCalls = 3;

    [Fact]
    public async Task Deliver_NoLeadSession_NothingSent_StaysPending()
    {
        using var r = new Rig();
        var id = r.Specs.Create("No lead", "owner/repo", "x").Id;
        var q = r.Inbox.Add(id, null, "Pick?", [], null, null, null, true, id + ":q1");
        var card = r.Inbox.Answer(q.Id, "blue");
        Assert.Null(await r.Relay.DeliverAsync(card, default));
        Assert.Empty(r.Owner.Sent);
        Assert.Equal("pending", r.Inbox.Get(q.Id).Answer!.RelayStatus);
    }

    [Fact]
    public async Task Poll_StaleSnapshot_DoesNotResendDeliveredCard()
    {
        using var r = new Rig();
        var id = r.NewSpec();
        var q = r.Inbox.Add(id, null, "Pick?", [], null, null, null, true, id + ":q1");
        var snapshot = r.Inbox.Answer(q.Id, "blue");
        r.Inbox.SetRelay(q.Id, "sent", null);
        Assert.Null(await r.Relay.DeliverAsync(snapshot, default));
        Assert.Empty(r.Owner.Sent);
    }

    [Fact]
    public async Task Poll_InactiveSpec_PendingCardNotSent()
    {
        using var r = new Rig();
        var id = r.NewSpec();
        var q = r.Inbox.Add(id, null, "Pick?", [], null, null, null, true, id + ":q1");
        r.Inbox.Answer(q.Id, "blue");
        r.Specs.Finish(id, SpecStatus.ReadyForHandoff, "b", null);
        await r.Runner.PollOnceAsync(default);
        Assert.Empty(r.Owner.Sent);
        var a = r.Inbox.Get(q.Id).Answer!;
        Assert.Equal(("pending", 0), (a.RelayStatus, a.RelayAttempts));
    }

    private static string StaleSpec(Rig r, SpecStatus status)
    {
        var id = r.Specs.Create("Add export button", "owner/repo", "x").Id;
        r.Specs.UpdateStatus(id, status);
        r.Time.Advance(TimeSpan.FromMinutes(3));
        return id;
    }

    [Fact]
    public async Task Reconcile_AdoptsExistingLead()
    {
        using var r = new Rig();
        var id = StaleSpec(r, SpecStatus.Queued);
        r.Cloud.SetByTag("fyrd-spec-" + id,
            FakeCloudSessions.Session("old", "running", ["fyrd-spec-" + id, "fyrd-role-lead"], T),
            FakeCloudSessions.Session("arch", "archived", ["fyrd-spec-" + id, "fyrd-role-lead"], T.AddHours(1)));
        await r.Runner.PollOnceAsync(default);
        var run = r.Specs.Get(id)!;
        Assert.Equal("old", run.SessionId);
        Assert.Equal(SpecStatus.Building, run.Status);
    }

    [Fact]
    public async Task Reconcile_AdoptsExistingResearchLead()
    {
        using var r = new Rig();
        var id = StaleSpec(r, SpecStatus.Researching);
        r.Cloud.SetByTag("fyrd-spec-" + id,
            FakeCloudSessions.Session("rl", "running", ["fyrd-spec-" + id, "fyrd-role-research-lead"], T),
            FakeCloudSessions.Session("lead", "running", ["fyrd-spec-" + id, "fyrd-role-lead"], T.AddHours(2)),
            FakeCloudSessions.Session("old", "archived", ["fyrd-spec-" + id, "fyrd-role-research-lead"], T.AddHours(1)));
        await r.Runner.PollOnceAsync(default);
        var run = r.Specs.Get(id)!;
        Assert.Equal("rl", run.SessionId);
        Assert.Equal(SpecStatus.Researching, run.Status);
    }

    [Fact]
    public async Task Reconcile_IgnoresResearchLeadFromEarlierRun()
    {
        using var r = new Rig();
        var id = StaleSpec(r, SpecStatus.Researching);
        r.Cloud.SetByTag("fyrd-spec-" + id,
            FakeCloudSessions.Session("first-run", "idle", ["fyrd-spec-" + id, "fyrd-role-research-lead"], T.AddHours(-2)));
        await r.Runner.PollOnceAsync(default);
        var run = r.Specs.Get(id)!;
        Assert.Equal(SpecStatus.Draft, run.Status);
        Assert.NotEqual("first-run", run.SessionId);
        Assert.Equal("Launch interrupted; try again", run.Error);
    }

    [Fact]
    public async Task Reconcile_NoSession_RevertsToDraftWithError()
    {
        using var r = new Rig();
        var id = StaleSpec(r, SpecStatus.Researching);
        await r.Runner.PollOnceAsync(default);
        var run = r.Specs.Get(id)!;
        Assert.Equal(SpecStatus.Draft, run.Status);
        Assert.Equal("Launch interrupted; try again", run.Error);
    }

    [Fact]
    public async Task Reconcile_RecentLaunch_Untouched()
    {
        using var r = new Rig();
        var id = r.Specs.Create("Add export button", "owner/repo", "x").Id;
        r.Specs.UpdateStatus(id, SpecStatus.Queued);
        r.Time.Advance(TimeSpan.FromSeconds(30));
        await r.Runner.PollOnceAsync(default);
        Assert.Equal(SpecStatus.Queued, r.Specs.Get(id)!.Status);
        Assert.Null(r.Specs.Get(id)!.Error);
    }

    [Fact]
    public async Task Poll_PeerWorking_NoCard_IsRunning()
    {
        using var r = new Rig();
        var id = r.NewSpec();
        r.Cloud.AddAssistantEvent("lead1", "e1", Units("[{\"id\":\"b\",\"title\":\"B\"}]"));
        r.Cloud.SetByTag("fyrd-spec-" + id,
            FakeCloudSessions.Session("p3", "running", ["fyrd-role-validator", "fyrd-unit-b"], T));
        await r.Runner.PollOnceAsync(default);
        var u = Assert.Single(r.Specs.ListUnits(id));
        Assert.Equal(UnitStage.Validate, u.Stage);
        Assert.Equal(UnitState.Running, u.State);
    }

    [Fact]
    public async Task Poll_UnitDone_StaysDone()
    {
        using var r = new Rig();
        var id = r.NewSpec();
        r.Cloud.AddAssistantEvent("lead1", "e1", Units("[{\"id\":\"a\",\"title\":\"A\",\"done\":true}]"));
        r.Cloud.SetByTag("fyrd-spec-" + id,
            FakeCloudSessions.Session("p1", "running", ["fyrd-role-implementer", "fyrd-unit-a"], T));
        await r.Runner.PollOnceAsync(default);
        var u = Assert.Single(r.Specs.ListUnits(id));
        Assert.Equal(UnitStage.Done, u.Stage);
        Assert.Equal(UnitState.Done, u.State);
    }

    [Fact]
    public async Task Poll_DoneBlock_SetsReadyForHandoffAndBranch()
    {
        using var r = new Rig();
        var id = r.NewSpec();
        r.Cloud.AddAssistantEvent("lead1", "e1", F + "fyrd-done\n{\"branch\":\"fyrd/export\"}\n" + F);
        await r.Runner.PollOnceAsync(default);
        var run = r.Specs.Get(id)!;
        Assert.Equal(SpecStatus.ReadyForHandoff, run.Status);
        Assert.Equal("fyrd/export", run.Branch);
        Assert.Equal("e1", run.LastEventId);
    }

    [Fact]
    public async Task Poll_FailedBlock_SetsFailed()
    {
        using var r = new Rig();
        var id = r.NewSpec();
        r.Cloud.AddAssistantEvent("lead1", "e1", F + "fyrd-failed\n{\"reason\":\"tests broke\"}\n" + F);
        await r.Runner.PollOnceAsync(default);
        var run = r.Specs.Get(id)!;
        Assert.Equal(SpecStatus.Failed, run.Status);
        Assert.Equal("tests broke", run.Error);
    }

    [Fact]
    public async Task Poll_NeedsActionWithoutQuestion_CreatesAttentionCard()
    {
        using var r = new Rig();
        var id = r.NewSpec();
        r.Cloud.AddAssistantEvent("lead1", "e1", "I need to run a command.");
        r.Cloud.SetSession(FakeCloudSessions.Session("lead1", "idle", needsAction: "Approve Bash: rm -rf x"));
        await r.Runner.PollOnceAsync(default);
        await r.Runner.PollOnceAsync(default);
        var card = Assert.Single(r.Inbox.ListOpenFor(id));
        Assert.Equal("Lead needs attention: Approve Bash: rm -rf x", card.Question);
        Assert.Equal(id + ":attention:e1", card.SourceKey);
        Assert.Equal("https://claude.ai/code/lead1", card.Details);
        Assert.True(card.AllowFreeText);
    }

    [Fact]
    public async Task Poll_NeedsActionWithQuestion_NoAttentionCard()
    {
        using var r = new Rig();
        var id = r.NewSpec();
        r.Cloud.AddAssistantEvent("lead1", "e1", Question("{\"id\":\"q1\",\"question\":\"Why?\"}"));
        r.Cloud.SetSession(FakeCloudSessions.Session("lead1", "idle", needsAction: "Waiting for input"));
        await r.Runner.PollOnceAsync(default);
        var card = Assert.Single(r.Inbox.ListOpenFor(id));
        Assert.Equal(id + ":q1", card.SourceKey);
    }

    [Fact]
    public async Task Poll_AuthFailure_SetsSignedOut_ThenRecovers()
    {
        using var r = new Rig();
        r.NewSpec();
        r.Cloud.ThrowOnNextCall(new CloudSessionAuthException(401));
        await r.Runner.PollOnceAsync(default);
        Assert.True(r.State.SignedOut);
        await r.Runner.PollOnceAsync(default);
        Assert.False(r.State.SignedOut);
        Assert.NotNull(r.State.LastPollAt);

        r.Cloud.ThrowOnNextCall(new ClaudeAuthMissingException());
        await r.Runner.PollOnceAsync(default);
        Assert.True(r.State.SignedOut);
    }

    [Fact]
    public async Task Poll_OneSpecThrows_OthersStillPolled()
    {
        using var r = new Rig();
        var bad = r.NewSpec("Bad spec", "lead-bad");
        var good = r.NewSpec("Good spec", "lead-good");
        r.Cloud.AddAssistantEvent("lead-bad", "b1", "x");
        r.Cloud.AddAssistantEvent("lead-good", "g1", Question("{\"id\":\"q1\",\"question\":\"Why?\"}"));
        // Whichever spec is polled first hits the failure; the other must still complete.
        r.Cloud.ThrowOnNextCall(new CloudSessionApiException(500, "boom"));
        await r.Runner.PollOnceAsync(default);
        Assert.NotNull(r.State.LastError);
        Assert.False(r.State.SignedOut);
        var cursors = new[] { r.Specs.Get(bad)!.LastEventId, r.Specs.Get(good)!.LastEventId };
        Assert.Single(cursors, c => c is not null);
    }

    [Fact]
    public async Task Poll_QuestionThenTextlessAssistantEvent_NoAttentionCard()
    {
        using var r = new Rig();
        var id = r.NewSpec();
        r.Cloud.AddAssistantEvent("lead1", "e1", Question("{\"id\":\"q1\",\"question\":\"Why?\"}"));
        r.Cloud.SetSession(FakeCloudSessions.Session("lead1", "idle", needsAction: "Waiting for input"));
        await r.Runner.PollOnceAsync(default);
        r.Inbox.Answer(Assert.Single(r.Inbox.ListOpenFor(id)).Id, "because");
        r.Cloud.AddEvent("lead1", new SessionEvent("e2", "assistant", DateTimeOffset.UnixEpoch, null, FakeCloudSessions.Empty));
        await r.Runner.PollOnceAsync(default);
        Assert.Empty(r.Inbox.ListOpenFor(id));
    }

    [Fact]
    public async Task Poll_OpenQuestionCard_NoAttentionCard()
    {
        using var r = new Rig();
        var id = r.NewSpec();
        r.Cloud.AddAssistantEvent("lead1", "e1", Question("{\"id\":\"q1\",\"question\":\"Why?\"}"));
        await r.Runner.PollOnceAsync(default);
        r.Cloud.AddAssistantEvent("lead1", "e2", "Still thinking.");
        r.Cloud.SetSession(FakeCloudSessions.Session("lead1", "idle", needsAction: "Waiting for input"));
        await r.Runner.PollOnceAsync(default);
        Assert.Equal(id + ":q1", Assert.Single(r.Inbox.ListOpenFor(id)).SourceKey);
    }

    [Fact]
    public async Task Poll_StatusCategoryCompleted_NoAttentionCard()
    {
        using var r = new Rig();
        var id = r.NewSpec();
        r.Cloud.AddAssistantEvent("lead1", "e1", "All done for now.");
        r.Cloud.SetSession(FakeCloudSessions.Session("lead1", "idle", needsAction: "Waiting", statusCategory: "completed"));
        await r.Runner.PollOnceAsync(default);
        Assert.Empty(r.Inbox.ListOpenFor(id));
    }

    [Fact]
    public async Task Poll_TimeoutOnOneSpec_OtherStillPolled()
    {
        using var r = new Rig();
        var a = r.NewSpec("Spec a", "lead-a");
        var b = r.NewSpec("Spec b", "lead-b");
        r.Cloud.AddAssistantEvent("lead-a", "a1", "x");
        r.Cloud.AddAssistantEvent("lead-b", "b1", "x");
        r.Cloud.ThrowOnNextCall(new TaskCanceledException("timeout"));
        await r.Runner.PollOnceAsync(default);
        Assert.NotNull(r.State.LastError);
        Assert.NotNull(r.State.LastPollAt);
        var cursors = new[] { r.Specs.Get(a)!.LastEventId, r.Specs.Get(b)!.LastEventId };
        Assert.Single(cursors, c => c is not null);
    }

    [Fact]
    public async Task Poll_CallerCancellation_StillPropagates()
    {
        using var r = new Rig();
        r.NewSpec();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => r.Runner.PollOnceAsync(cts.Token));
    }

    [Fact]
    public async Task Poll_MalformedBlock_SurfacesWarningInLastError()
    {
        using var r = new Rig();
        var id = r.NewSpec();
        r.Cloud.AddAssistantEvent("lead1", "e1", F + "fyrd-done" + (char)10 + "{\"nobranch\":1}" + (char)10 + F);
        await r.Runner.PollOnceAsync(default);
        Assert.StartsWith($"Ignored malformed fyrd block in {id}: fyrd-done: missing branch", r.State.LastError);
    }

    private static string Draft(string json) => F + "fyrd-spec-draft\n" + json + "\n" + F;

    private static string ResearchSpec(Rig r)
    {
        var id = r.Specs.Create("Research me", "owner/repo", "x").Id;
        r.Specs.SetResearchLead(id, "rl1");
        r.Cloud.SetSession(FakeCloudSessions.Session("rl1", "running"));
        return id;
    }

    private static async Task<string> ResearchToDraftAsync(Rig r)
    {
        var id = ResearchSpec(r);
        r.Cloud.AddAssistantEvent("rl1", "e1", Draft("{\"markdown\":\"# S\",\"summary\":\"sum\",\"openQuestions\":[]}"));
        await r.Runner.PollOnceAsync(default);
        r.Specs.DiscardProposal(id);
        return id;
    }

    [Fact]
    public async Task Attack_AfterResearch_ClearsOldSession()
    {
        using var r = new Rig();
        var id = await ResearchToDraftAsync(r);
        var run = r.Specs.TryClaim(id, SpecStatus.Queued)!;
        Assert.Null(run.SessionId);
        Assert.Null(run.LastEventId);
        var calls = r.Cloud.EventAfterIds.Count;
        r.Cloud.SetSession(FakeCloudSessions.Session("rl1", "idle", needsAction: "waiting"));
        await r.Runner.PollOnceAsync(default);
        Assert.Equal(calls, r.Cloud.EventAfterIds.Count);
        Assert.Empty(r.Inbox.ListOpen());
    }

    [Fact]
    public async Task Reconcile_AfterResearch_RevertsToDraft()
    {
        using var r = new Rig();
        var id = await ResearchToDraftAsync(r);
        r.Specs.TryClaim(id, SpecStatus.Researching);
        r.Time.Advance(TimeSpan.FromMinutes(3));
        await r.Runner.PollOnceAsync(default);
        var run = r.Specs.Get(id)!;
        Assert.Equal(SpecStatus.Draft, run.Status);
        Assert.Equal("Launch interrupted; try again", run.Error);
    }

    [Fact]
    public async Task Poll_SpecDraft_SavesProposalAndReturnsToDraft()
    {
        using var r = new Rig();
        var id = ResearchSpec(r);
        r.Cloud.AddAssistantEvent("rl1", "e1",
            Draft("{\"markdown\":\"# S\",\"summary\":\"sum\",\"openQuestions\":[\"a?\"]}"));
        await r.Runner.PollOnceAsync(default);
        var run = r.Specs.Get(id)!;
        Assert.Equal(SpecStatus.Draft, run.Status);
        Assert.Null(run.Error);
        Assert.True(run.HasProposal);
        var p = r.Specs.GetProposal(id)!;
        Assert.Equal("# S", p.Markdown);
        Assert.Equal("sum", p.Summary);
        Assert.Equal("rl1", p.SessionId);
        Assert.Equal("e1", run.LastEventId);
    }

    [Fact]
    public async Task Poll_SpecDraftWithoutMarkdown_SetsError()
    {
        using var r = new Rig();
        var id = ResearchSpec(r);
        r.Cloud.AddAssistantEvent("rl1", "e1", Draft("{\"summary\":\"s\"}"));
        await r.Runner.PollOnceAsync(default);
        var run = r.Specs.Get(id)!;
        Assert.Equal(SpecStatus.Draft, run.Status);
        Assert.Equal("Research returned no spec", run.Error);
        Assert.Null(r.Specs.GetProposal(id));
    }

    [Fact]
    public async Task Poll_ResearchFailed_ReturnsToDraftWithReason()
    {
        using var r = new Rig();
        var id = ResearchSpec(r);
        r.Cloud.AddAssistantEvent("rl1", "e1", F + "fyrd-failed\n{\"reason\":\"no repo access\"}\n" + F);
        await r.Runner.PollOnceAsync(default);
        var run = r.Specs.Get(id)!;
        Assert.Equal(SpecStatus.Draft, run.Status);
        Assert.Equal("no repo access", run.Error);
    }

    [Fact]
    public async Task Poll_ResearcherPeers_StageResearch()
    {
        using var r = new Rig();
        var id = ResearchSpec(r);
        r.Cloud.SetByTag("fyrd-spec-" + id,
            FakeCloudSessions.Session("c1", "running", ["fyrd-role-researcher", "fyrd-unit-codebase"]),
            FakeCloudSessions.Session("a1", "idle", ["fyrd-role-researcher", "fyrd-unit-approach"]),
            FakeCloudSessions.Session("k1", "running", ["fyrd-role-researcher", "fyrd-unit-risks"]));
        r.Inbox.Add(id, "risks", "Q?", [], null, null, null, true, id + ":q");
        await r.Runner.PollOnceAsync(default);
        var units = r.Specs.ListUnits(id).ToDictionary(u => u.Id);
        Assert.Equal(3, units.Count);
        Assert.All(units.Values, u => Assert.Equal(UnitStage.Research, u.Stage));
        Assert.Equal(UnitState.Running, units["codebase"].State);
        Assert.Equal(UnitState.Idle, units["approach"].State);
        Assert.Equal(UnitState.WaitingOnOwner, units["risks"].State);
    }

    [Fact]
    public async Task Poll_ResearchUnitsDoneWhenLeavingResearching()
    {
        using var r = new Rig();
        var id = ResearchSpec(r);
        r.Cloud.SetByTag("fyrd-spec-" + id,
            FakeCloudSessions.Session("c1", "running", ["fyrd-role-researcher", "fyrd-unit-codebase"]));
        await r.Runner.PollOnceAsync(default);
        r.Cloud.AddAssistantEvent("rl1", "e1", Draft("{\"markdown\":\"# S\"}"));
        await r.Runner.PollOnceAsync(default);
        Assert.All(r.Specs.ListUnits(id), u => Assert.Equal(UnitStage.Done, u.Stage));
    }

    [Fact]
    public async Task Poll_ResearchLeadDone_Ignored()
    {
        using var r = new Rig();
        var id = ResearchSpec(r);
        r.Cloud.AddAssistantEvent("rl1", "e1", F + "fyrd-done\n{\"branch\":\"b\"}\n" + F);
        await r.Runner.PollOnceAsync(default);
        Assert.Equal(SpecStatus.Researching, r.Specs.Get(id)!.Status);
        Assert.Contains("fyrd-done", r.State.LastError);
    }

    [Fact]
    public async Task Poll_BuildLeadSpecDraft_Ignored()
    {
        using var r = new Rig();
        var id = r.NewSpec();
        r.Cloud.AddAssistantEvent("lead1", "e1", Draft("{\"markdown\":\"# S\"}"));
        await r.Runner.PollOnceAsync(default);
        var run = r.Specs.Get(id)!;
        Assert.Equal(SpecStatus.Building, run.Status);
        Assert.False(run.HasProposal);
        Assert.Contains("fyrd-spec-draft", r.State.LastError);
    }

    private sealed class RacingCloud(FakeCloudSessions inner, Action onListEvents) : ICloudSessions
    {
        public Task<CloudSession> CreateAsync(CreateSessionRequest r, CancellationToken ct = default) => inner.CreateAsync(r, ct);
        public Task<CloudSession> GetAsync(string id, CancellationToken ct = default) => inner.GetAsync(id, ct);
        public Task<EventPage> ListEventsAsync(string id, int limit = 100, string? after = null, string? before = null, CancellationToken ct = default)
        {
            onListEvents();
            return inner.ListEventsAsync(id, limit, after, before, ct);
        }
        public Task SendMessageAsync(string id, string text, CancellationToken ct = default) => inner.SendMessageAsync(id, text, ct);
        public Task<IReadOnlyList<CloudSession>> ListByTagAsync(string tag, CancellationToken ct = default) => inner.ListByTagAsync(tag, ct);
        public Task InterruptAsync(string id, CancellationToken ct = default) => inner.InterruptAsync(id, ct);
        public Task ArchiveAsync(string id, CancellationToken ct = default) => inner.ArchiveAsync(id, ct);
        public Task RenameAsync(string id, string title, CancellationToken ct = default) => inner.RenameAsync(id, title, ct);
        public Task AnswerQuestionAsync(string id, Fyrd.Core.Hub.HubQuestion q, IReadOnlyDictionary<string, string> answers, CancellationToken ct = default) =>
            inner.AnswerQuestionAsync(id, q, answers, ct);
        public IAsyncEnumerable<StreamedEvent> StreamEventsAsync(string id, long? after, CancellationToken ct = default) =>
            inner.StreamEventsAsync(id, after, ct);
    }

    [Fact]
    public async Task Poll_SpecDraftWhenSpecNoLongerResearching_WarnsAndSavesNothing()
    {
        using var r = new Rig();
        var id = ResearchSpec(r);
        r.Cloud.AddAssistantEvent("rl1", "e1", Draft("{\"markdown\":\"# S\"}"));
        // The runner snapshotted the spec as Researching; the owner abandons it before the batch is applied.
        var racing = new RacingCloud(r.Cloud, () => r.Specs.Finish(id, SpecStatus.Draft, null, null));
        var runner = new SpecRunner(r.Specs, r.Inbox, racing, r.State, r.Time, r.Relay);
        await runner.PollOnceAsync(default);
        var run = r.Specs.Get(id)!;
        Assert.Equal(SpecStatus.Draft, run.Status);
        Assert.False(run.HasProposal);
        Assert.Null(r.Specs.GetProposal(id));
        Assert.Contains("Ignored spec draft", r.State.LastError);
        // Deliberate: the cursor still advances so the consumed draft is not replayed forever.
        Assert.Equal("e1", run.LastEventId);
    }

    [Fact]
    public async Task Poll_DraftAndQuestionInSameTurn_SavesProposalNoCard()
    {
        using var r = new Rig();
        var id = ResearchSpec(r);
        r.Cloud.AddAssistantEvent("rl1", "e1",
            Question("{\"id\":\"q1\",\"question\":\"Stale?\"}") + "\n" + Draft("{\"markdown\":\"# S\"}"));
        await r.Runner.PollOnceAsync(default);
        Assert.True(r.Specs.Get(id)!.HasProposal);
        Assert.Empty(r.Inbox.ListOpenFor(id));
    }

    [Fact]
    public async Task Poll_FailedAndQuestionInSameTurn_NoCard()
    {
        using var r = new Rig();
        var id = ResearchSpec(r);
        r.Cloud.AddAssistantEvent("rl1", "e1",
            Question("{\"id\":\"q1\",\"question\":\"Stale?\"}") + "\n" + F + "fyrd-failed\n{\"reason\":\"x\"}\n" + F);
        await r.Runner.PollOnceAsync(default);
        Assert.Equal(SpecStatus.Draft, r.Specs.Get(id)!.Status);
        Assert.Empty(r.Inbox.ListOpenFor(id));
    }

    [Fact]
    public async Task Poll_InvalidDraftAndQuestionInSameTurn_NoCard()
    {
        using var r = new Rig();
        var id = ResearchSpec(r);
        r.Cloud.AddAssistantEvent("rl1", "e1",
            Question("{\"id\":\"q1\",\"question\":\"Stale?\"}") + "\n" + Draft("{\"summary\":\"s\"}"));
        await r.Runner.PollOnceAsync(default);
        Assert.Equal("Research returned no spec", r.Specs.Get(id)!.Error);
        Assert.Empty(r.Inbox.ListOpenFor(id));
    }
}

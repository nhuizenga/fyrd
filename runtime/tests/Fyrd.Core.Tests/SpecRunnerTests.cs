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
        public RunnerState State = new();
        public SpecRunner Runner;

        public Rig()
        {
            Specs = new SpecStore(Home.Paths, Time);
            Inbox = new InboxStore(Home.Paths, Specs, new DecisionMemory(Home.Paths), Time);
            Runner = NewRunner();
        }

        public SpecRunner NewRunner() => new(Specs, Inbox, Cloud, State, Time, Relay);
        public LeadRelay Relay => new(Specs, Cloud, State, Inbox);

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

        Assert.Single(r.Cloud.Sent);
        Assert.Equal("sent", r.Inbox.Get(q.Id).Answer!.RelayStatus);
    }

    [Fact]
    public async Task Poll_FailedRelay_RecordsFailureAndRetries()
    {
        using var r = new Rig();
        var id = r.NewSpec();
        var q = r.Inbox.Add(id, null, "Pick?", [], null, null, null, true, id + ":q1");
        var card = r.Inbox.SetRelay(r.Inbox.Answer(q.Id, "blue").Id, "pending", null);
        r.Cloud.ThrowOnNextCall(new HttpRequestException("boom"));
        Assert.Equal("boom", await r.Relay.DeliverAsync(card, default));
        var failed = r.Inbox.Get(q.Id).Answer!;
        Assert.Equal("failed", failed.RelayStatus);
        Assert.Equal("boom", failed.RelayError);
        await r.Runner.PollOnceAsync(default);
        Assert.Equal("sent", r.Inbox.Get(q.Id).Answer!.RelayStatus);
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
}

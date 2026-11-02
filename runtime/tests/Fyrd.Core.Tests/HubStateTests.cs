using System.Text.Json;
using Fyrd.Core.Hub;

namespace Fyrd.Core.Tests;

public sealed class HubStateTests
{
    private readonly FixedTime _time = new(DateTimeOffset.Parse("2026-10-31T12:00:00Z"));

    private static readonly JsonElement Input = JsonDocument.Parse("{}").RootElement.Clone();

    private static HubQuestion Q(string id, string requestId, string text) =>
        new(id, "s1", "t", text, ["A", "B"], false, DateTimeOffset.UnixEpoch, "toolu", requestId, Input);

    [Fact]
    public void RecordAnswer_TwoQuestionRequest_WaitsThenReturnsBoth()
    {
        var s = new HubState(_time);
        s.Publish([], [Q("q1", "r1", "First?"), Q("q2", "r1", "Second?")]);

        var first = s.RecordAnswer("q1", "A");
        Assert.True(first.Pending);
        Assert.Equal(1, first.WaitingFor);
        Assert.False(first.Ready);

        var second = s.RecordAnswer("q2", "B");
        Assert.True(second.Ready);
        Assert.Equal("A", second.Answers!["First?"]);
        Assert.Equal("B", second.Answers!["Second?"]);
    }

    [Fact]
    public void RecordAnswer_Unknown_NotPending() =>
        Assert.False(new HubState(_time).RecordAnswer("nope", "A").Pending);

    [Fact]
    public void TryClaim_IsExclusive_UntilReleased()
    {
        var s = new HubState(_time);
        Assert.True(s.TryClaim("r1"));
        Assert.False(s.TryClaim("r1"));
        s.Release("r1");
        Assert.True(s.TryClaim("r1"));
    }

    [Fact]
    public void Publish_ClaimedRequest_StaysHidden_UntilPollStopsSeeingIt()
    {
        var s = new HubState(_time);
        var q = Q("q1", "r1", "First?");
        s.Publish([], [q]);
        Assert.True(s.TryClaim("r1"));
        s.CompleteRequest("r1");
        Assert.Empty(s.Questions);

        s.Publish([], [q]); // a stale poll pass still sees it pending
        Assert.Empty(s.Questions);
        Assert.False(s.RecordAnswer("q1", "A").Pending);

        s.Publish([], []); // the control_response is visible: claim expires
        Assert.True(s.TryClaim("r1"));
    }

    [Fact]
    public void Publish_FailedSession_KeepsClaim_ThenPendingStaysHidden()
    {
        var s = new HubState(_time);
        var q = Q("q1", "r1", "First?");
        s.Publish([], [q]);
        Assert.True(s.TryClaim("r1", "s1"));
        s.CompleteRequest("r1");

        s.Publish([], [], new HashSet<string> { "s1" }); // s1's poll failed: it saw nothing
        s.Publish([], [q]); // next poll: still pending in the cloud
        Assert.Empty(s.Questions);
        Assert.False(s.RecordAnswer("q1", "B").Pending);
        Assert.False(s.TryClaim("r1"));
    }

    [Fact]
    public void Publish_FailedSession_TtlStillApplies()
    {
        var s = new HubState(_time);
        var q = Q("q1", "r1", "First?");
        s.Publish([], [q]);
        s.TryClaim("r1", "s1");
        _time.Advance(HubState.ClaimTtl + TimeSpan.FromSeconds(1));
        s.Publish([], [q], new HashSet<string> { "s1" });
        Assert.Single(s.Questions);
    }

    [Fact]
    public void Publish_ClaimOlderThanTtl_Expires()
    {
        var s = new HubState(_time);
        var q = Q("q1", "r1", "First?");
        s.Publish([], [q]);
        s.TryClaim("r1");
        _time.Advance(HubState.ClaimTtl + TimeSpan.FromSeconds(1));
        s.Publish([], [q]);
        Assert.Single(s.Questions);
    }

    [Fact]
    public void RequestPoll_CompletesUntilReset()
    {
        var s = new HubState();
        Assert.False(s.PollRequested.IsCompleted);
        s.RequestPoll();
        s.RequestPoll();
        Assert.True(s.PollRequested.IsCompleted);
        s.ResetPollRequest();
        Assert.False(s.PollRequested.IsCompleted);
    }

    [Theory]
    [InlineData("fyrd-workers-running", true)]
    [InlineData("fyrd-hub", false)]
    public void SessionView_WorkersRunning_ReadsTheTag(string tag, bool expected)
    {
        var view = new HubSessionView("s1", "builder-1", "builder", "idle", null, null, null, null, ["fyrd-hub", tag]);
        Assert.Equal(expected, view.WorkersRunning);
    }
}

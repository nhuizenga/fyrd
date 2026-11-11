using System.Text.Json;
using Fyrd.Core.CloudSessions;
using Fyrd.Core.Hub;

namespace Fyrd.Core.Tests;

public sealed class HubNotificationsTests
{
    private const string Ask = "Should I add these as comments or suggested edits?";

    private static CloudSession Session(string status = "idle", string? category = "need_input", string? needs = Ask) =>
        new("s1", "reviewer-doc", status, "connected", new PostTurnSummary(category, needs, null, null), ["fyrd-hub"],
            FakeCloudSessions.Empty);

    private static SessionEvent Ev(string uuid, string type, string? text = null, string raw = "{}", int at = 0) =>
        new(uuid, type, DateTimeOffset.Parse("2026-11-11T01:28:00Z").AddSeconds(at), text,
            JsonDocument.Parse(raw).RootElement.Clone());

    private static readonly SessionEvent[] Turn =
    [
        Ev("u1", "user", "Accept it.", at: 0),
        Ev("a1", "assistant", "Earlier text.", at: 5),
        Ev("a2", "assistant", "That settles it. " + Ask, at: 30),
        Ev("r1", "result", raw: """{"type":"result","result":"That settles it. Should I add these as comments or suggested edits?"}""", at: 33),
    ];

    [Fact]
    public void Find_IdleNeedInputWithNeedsAction_IsOnePerTurn()
    {
        var n = HubNotifications.Find(Session(), "reviewer-doc", Turn);

        Assert.NotNull(n);
        Assert.Equal("s1:r1", n.Id);
        Assert.Equal("s1", n.SessionId);
        Assert.Equal("reviewer-doc", n.SessionTitle);
        Assert.Equal(Ask, n.Headline);
        Assert.Equal("That settles it. " + Ask, n.Text);
        Assert.Equal(DateTimeOffset.Parse("2026-11-11T01:28:33Z"), n.At);
    }

    [Theory]
    [InlineData("running", "need_input", Ask)]
    [InlineData("requires_action", "need_input", Ask)]
    [InlineData("idle", "review_ready", Ask)]
    [InlineData("idle", "completed", "")]
    [InlineData("idle", "need_input", "")]
    [InlineData("idle", "need_input", "none")]
    [InlineData("idle", null, null)]
    public void Find_NotWaitingOnAPlainTextQuestion_IsNull(string status, string? category, string? needs) =>
        Assert.Null(HubNotifications.Find(Session(status, category, needs), "t", Turn));

    [Fact]
    public void Find_NoResult_UsesNewestEventAndLastMainAgentText()
    {
        SessionEvent[] events =
        [
            Ev("a1", "assistant", "Main reply.", at: 1),
            Ev("a2", "assistant", "A worker's reply.", raw: """{"parent_tool_use_id":"toolu_1"}""", at: 2),
        ];

        var n = HubNotifications.Find(Session(), "t", events);

        Assert.Equal("s1:a2", n!.Id);
        Assert.Equal("Main reply.", n.Text);
    }

    [Fact]
    public void Find_NoEvents_IsNull() => Assert.Null(HubNotifications.Find(Session(), "t", []));

    [Fact]
    public void Dismissals_PersistAcrossInstances()
    {
        using var h = new TempHome();
        new NotificationDismissals(h.Paths).Dismiss("s1:r1");

        var reopened = new NotificationDismissals(h.Paths);

        Assert.True(reopened.Contains("s1:r1"));
        Assert.False(reopened.Contains("s1:r2"));
    }

    [Fact]
    public void State_DroppedNotificationStaysGoneUntilAPassNoLongerFindsIt()
    {
        var state = new HubState();
        var n = HubNotifications.Find(Session(), "t", Turn)!;
        state.PublishNotifications([n]);

        Assert.True(state.DropNotification(n.Id));
        state.PublishNotifications([n]);
        Assert.Empty(state.Notifications);

        // A pass without it forgets the drop, so a later turn with the same id (none in practice) would show again.
        state.PublishNotifications([]);
        state.PublishNotifications([n]);
        Assert.Single(state.Notifications);
    }

    [Fact]
    public void State_DropNotificationsFor_RemovesOnlyThatSession()
    {
        var state = new HubState();
        var a = new HubNotification("s1:r1", "s1", "a", "h", null, DateTimeOffset.UnixEpoch);
        var b = new HubNotification("s2:r1", "s2", "b", "h", null, DateTimeOffset.UnixEpoch);
        state.PublishNotifications([a, b]);

        Assert.Equal(["s1:r1"], state.DropNotificationsFor("s1"));
        Assert.Equal(["s2:r1"], state.Notifications.Select(n => n.Id));
        Assert.False(state.DropNotification("s1:r1"));
    }
}

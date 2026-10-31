using Fyrd.Core.Orchestration;
using Fyrd.Core.Specs;

namespace Fyrd.Core.Tests;

public class LeadRelayTests
{
    private const string Repo = "servicetitan/titan-lens-ossie";

    private static (SpecStore Specs, FakeCloudSessions Cloud, LeadRelay Relay, SpecRun Run) Setup(TempHome h, bool lead = true)
    {
        var specs = new SpecStore(h.Paths, new FixedTime(DateTimeOffset.Parse("2026-10-29T12:00:00Z")));
        var run = specs.Create("Add export", Repo, "x");
        if (lead) { specs.SetLead(run.Id, "sess-" + run.Id); specs.UpdateStatus(run.Id, SpecStatus.Building); }
        var cloud = new FakeCloudSessions();
        return (specs, cloud, new LeadRelay(specs, cloud, new RunnerState(), new InboxStore(h.Paths, specs, new DecisionMemory(h.Paths), new FixedTime(DateTimeOffset.Parse("2026-10-29T12:00:00Z")))), run);
    }

    private static InboxCard Card(string specId, string? key) =>
        new("c1", specId, null, "Q?", [], null, null, null, true, DateTimeOffset.UnixEpoch,
            new CardAnswer("blue", true, MemoryScope.Repo, DateTimeOffset.UnixEpoch), key);

    [Fact]
    public async Task RelayAnswer_SendsFyrdAnswerBlockToLead()
    {
        using var h = new TempHome(); var (_, cloud, relay, run) = Setup(h);
        await relay.RelayAnswerAsync(Card(run.Id, run.Id + ":q-color"), default);
        var sent = Assert.Single(cloud.Sent);
        Assert.Equal("sess-" + run.Id, sent.SessionId);
        Assert.Contains("```fyrd-answer", sent.Text);
        Assert.Contains("\"id\":\"q-color\"", sent.Text);
        Assert.Contains("\"answer\":\"blue\"", sent.Text);
    }

    [Fact]
    public async Task RelayAnswer_AttentionCard_SendsPlainText()
    {
        using var h = new TempHome(); var (_, cloud, relay, run) = Setup(h);
        await relay.RelayAnswerAsync(Card(run.Id, run.Id + ":attention:ev1"), default);
        Assert.Equal("blue", Assert.Single(cloud.Sent).Text);
    }

    [Fact]
    public async Task RelayAnswer_NoSourceKey_SendsNothing()
    {
        using var h = new TempHome(); var (_, cloud, relay, run) = Setup(h);
        await relay.RelayAnswerAsync(Card(run.Id, null), default);
        Assert.Empty(cloud.Sent);
    }

    [Fact]
    public async Task RelayAnswer_NoLead_SendsNothing()
    {
        using var h = new TempHome(); var (_, cloud, relay, run) = Setup(h, lead: false);
        await relay.RelayAnswerAsync(Card(run.Id, run.Id + ":q1"), default);
        Assert.Empty(cloud.Sent);
    }

    [Fact]
    public async Task RelayConfig_OnlyActiveLeads()
    {
        using var h = new TempHome(); var (specs, cloud, relay, run) = Setup(h);
        var done = specs.Create("Other thing", Repo, "x");
        specs.SetLead(done.Id, "sess-done"); specs.UpdateStatus(done.Id, SpecStatus.ReadyForHandoff);
        await relay.RelayConfigAsync(6, default);
        var sent = Assert.Single(cloud.Sent);
        Assert.Equal("sess-" + run.Id, sent.SessionId);
        Assert.Contains("```fyrd-config", sent.Text);
        Assert.Contains("\"squadCap\":6", sent.Text);
    }
}

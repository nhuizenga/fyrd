using System.Text.Json;
using Fyrd.Core.CloudSessions;
using Fyrd.Core.Config;
using Fyrd.Core.Hub;

namespace Fyrd.Core.Tests;

public sealed class CostGuardTests : IDisposable
{
    private const string Ctl = "session_ctl";
    private const string Peasant = "session_p1";
    private const string Other = "session_p2";

    private readonly TempHome _home = new();
    private readonly string _seeds = Path.Combine(Path.GetTempPath(), "fyrd-seeds-" + Guid.NewGuid());
    private readonly FakeCloudSessions _cloud = new();
    private readonly FakeOwnerMessages _owner = new();
    private readonly FixedTime _time = new(DateTimeOffset.Parse("2026-11-01T12:00:00Z"));
    private readonly ConfigStore _config;
    private readonly ControllerManager _controller;
    private readonly CostLedger _ledger;
    private readonly CostGuard _guard;

    public CostGuardTests()
    {
        Directory.CreateDirectory(_seeds);
        File.WriteAllText(Path.Combine(_seeds, "controller.md"), "---\nname: Controller\nmodel: claude-opus-5-5\nlocked: true\n---\nControl.");
        _config = new ConfigStore(_home.Paths);
        _config.Save(new FyrdConfig(EnvironmentId: "env_abc123", TimeZone: "UTC"));
        _controller = new ControllerManager(_cloud, _owner, new HubPersonas(_home.Paths, _seeds), new HubMemory(_home.Paths), _config, _home.Paths);
        _ledger = new CostLedger(_home.Paths, _config, _time);
        _guard = new CostGuard(_ledger, _config, _cloud, _owner, _controller);
    }

    public void Dispose()
    {
        _home.Dispose();
        if (Directory.Exists(_seeds)) Directory.Delete(_seeds, true);
    }

    private async Task CreateController()
    {
        _cloud.SetSession(FakeCloudSessions.Session(Ctl));
        _owner.CreatedIds.Enqueue(Ctl);
        await _controller.CreateAsync(default);
        _owner.Sent.Clear();
    }

    private void Spend(string id, decimal cost, string uuid = "")
    {
        var raw = JsonDocument.Parse($"{{\"model_usage_since_last_result\":{{\"m\":{{\"costUSD\":{cost}}}}}}}").RootElement.Clone();
        _ledger.Feed(id, [new SessionEvent(uuid.Length > 0 ? uuid : Guid.NewGuid().ToString(), "result", _time.GetUtcNow(), null, raw)]);
    }

    private sealed record Ran(IReadOnlyDictionary<string, HubCostView> Views, HubBudgetView Budget, string? Error);

    private async Task<Ran> Run(bool controllerLive = true, params string[] ids)
    {
        var live = ids.Select(i => (i, i)).ToList();
        var pass = _guard.Build(ids);
        var error = await _guard.EnforceAsync(live, pass, controllerLive, default);
        return new Ran(pass.Views, pass.Budget, error);
    }

    private void Limits(decimal session = 50, decimal fyrd = 500) =>
        _config.Save(_config.Load() with { SessionDailyLimitUsd = session, FyrdDailyLimitUsd = fyrd });

    [Fact]
    public async Task UnderLimit_NothingHappens()
    {
        await CreateController();
        Spend(Peasant, 10);
        var pass = await Run(true, Peasant, Ctl);
        var v = pass.Views[Peasant];
        Assert.Null(v.Blocked);
        Assert.Equal(0.2, v.Ratio, 3);
        Assert.False(v.CanExtend);
        Assert.Empty(_cloud.Interrupted);
        Assert.Empty(_owner.Sent);
    }

    [Fact]
    public async Task CrossingLimit_InterruptsOnce_AndMessagesControllerOnce()
    {
        await CreateController();
        Spend(Peasant, 51);
        for (var i = 0; i < 3; i++)
        {
            var pass = await Run(true, Peasant, Ctl);
            Assert.Equal("session", pass.Views[Peasant].Blocked);
        }
        Assert.Equal([Peasant], _cloud.Interrupted);
        var sent = Assert.Single(_owner.Sent);
        Assert.Equal(Ctl, sent.SessionId);
        Assert.Contains("Fyrd budget: session session_p1 (session_p1) reached its 50.00 USD daily limit (51.00 USD spent)", sent.Text);
    }

    [Fact]
    public async Task FailedInterrupt_RetriesNextPass_WithoutMarker()
    {
        await CreateController();
        Spend(Peasant, 60);
        _cloud.ThrowOnNextCall(new HttpRequestException("boom"));
        var first = await Run(true, Peasant, Ctl);
        Assert.Contains("boom", first.Error);
        Assert.Null(_ledger.NotifiedAtLimit(Peasant));
        Assert.Empty(_cloud.Interrupted);

        var second = await Run(true, Peasant, Ctl);
        Assert.Null(second.Error);
        Assert.Equal([Peasant], _cloud.Interrupted);
        Assert.Single(_owner.Sent);
        Assert.Equal(50m, _ledger.NotifiedAtLimit(Peasant));
    }

    [Fact]
    public async Task FailedControllerMessage_RetriesBoth()
    {
        await CreateController();
        Spend(Peasant, 60);
        _owner.ThrowOnNextCall(new HttpRequestException("send failed")); // the interrupt succeeds, the notice fails
        var first = await Run(true, Peasant, Ctl);
        Assert.NotNull(first.Error);
        Assert.Null(_ledger.NotifiedAtLimit(Peasant));
        await Run(true, Peasant, Ctl);
        Assert.Equal(50m, _ledger.NotifiedAtLimit(Peasant));
        Assert.Single(_owner.Sent);
        Assert.Equal([Peasant], _cloud.Interrupted); // the retry only sends: no second interrupt
    }

    [Fact]
    public async Task ControllerExistsButNotLive_NotifiedLater_WithoutReInterrupt()
    {
        await CreateController();
        Spend(Peasant, 60);
        await Run(false, Peasant); // the Controller could not be read this pass
        Assert.Equal([Peasant], _cloud.Interrupted);
        Assert.Empty(_owner.Sent);
        Assert.Null(_ledger.NotifiedAtLimit(Peasant));

        await Run(true, Peasant, Ctl);
        await Run(true, Peasant, Ctl);
        Assert.Equal([Peasant], _cloud.Interrupted);
        Assert.Single(_owner.Sent);
    }

    [Fact]
    public async Task FyrdBlocked_NoRaiseOffered()
    {
        Limits(fyrd: 60);
        Spend(Peasant, 46);
        Spend(Other, 20);
        var pass = await Run(false, Peasant, Other);
        Assert.False(pass.Views[Peasant].CanExtend);
        Assert.Equal(CostGuard.ExtendStatus.FyrdBlocked, _guard.TryExtend(Peasant, 25, [Peasant, Other]).Status);
        Assert.Equal(0m, _ledger.ExtraUsd(Peasant));
    }

    [Fact]
    public void TryExtend_SecondRaiseWithoutNewSpend_Refused()
    {
        Spend(Peasant, 46);
        Assert.Equal(CostGuard.ExtendStatus.Ok, _guard.TryExtend(Peasant, 25, [Peasant]).Status);
        Assert.Equal(CostGuard.ExtendStatus.TooEarly, _guard.TryExtend(Peasant, 25, [Peasant]).Status);
        Assert.Equal(25m, _ledger.ExtraUsd(Peasant));
    }

    [Fact]
    public async Task SaveFailure_ReportedAndRetried_ViewsStillBuilt()
    {
        Spend(Peasant, 10);
        Directory.CreateDirectory(Path.Combine(_home.Paths.HubDir, "costs.json")); // makes every write fail
        var pass = await Run(false, Peasant);
        Assert.Contains("costs.json", pass.Error);
        Assert.Equal(10m, pass.Views[Peasant].CostToday);
        Directory.Delete(Path.Combine(_home.Paths.HubDir, "costs.json"));
        Assert.Null((await Run(false, Peasant)).Error);
        Assert.Equal(10m, new CostLedger(_home.Paths, _config, _time).TodayCost(Peasant).Cost);
    }

    [Fact]
    public async Task Raise_Unblocks_AndCrossingRaisedLimitInterruptsAgain()
    {
        await CreateController();
        Spend(Peasant, 46);
        var pass = await Run(true, Peasant, Ctl);
        Assert.True(pass.Views[Peasant].CanExtend); // 92%
        Assert.Empty(_cloud.Interrupted);

        _ledger.AddExtra(Peasant, 25);
        pass = await Run(true, Peasant, Ctl);
        Assert.Equal(75m, pass.Views[Peasant].LimitUsd);
        Assert.Null(pass.Views[Peasant].Blocked);

        Spend(Peasant, 30); // 76 >= 75
        pass = await Run(true, Peasant, Ctl);
        Assert.Equal("session", pass.Views[Peasant].Blocked);
        Assert.Equal([Peasant], _cloud.Interrupted);

        Spend(Peasant, 1);
        _ledger.AddExtra(Peasant, 25);
        Spend(Peasant, 25); // 102 >= 100
        await Run(true, Peasant, Ctl);
        Assert.Equal([Peasant, Peasant], _cloud.Interrupted);
        Assert.Equal(2, _owner.Sent.Count);
    }

    [Fact]
    public async Task CanExtend_AtNinetyPercent_NotBefore()
    {
        Spend(Peasant, 44.99m);
        Assert.False((await Run(false, Peasant)).Views[Peasant].CanExtend);
        Spend(Peasant, 0.01m);
        Assert.True((await Run(false, Peasant)).Views[Peasant].CanExtend);
    }

    [Fact]
    public async Task Controller_OverSessionLimit_NeverInterruptedOrBlocked()
    {
        await CreateController();
        Spend(Ctl, 80);
        var pass = await Run(true, Ctl);
        var v = pass.Views[Ctl];
        Assert.True(v.Exempt);
        Assert.Null(v.Blocked);
        Assert.False(v.CanExtend);
        Assert.Empty(_cloud.Interrupted);
        Assert.Empty(_owner.Sent);
    }

    [Fact]
    public async Task FyrdOverLimit_BlocksEveryoneButController_NoInterrupts_MessagesOnce()
    {
        await CreateController();
        Limits(fyrd: 100);
        Spend(Peasant, 40);
        Spend(Other, 30);
        Spend(Ctl, 40);
        for (var i = 0; i < 3; i++)
        {
            var pass = await Run(true, Peasant, Other, Ctl);
            Assert.True(pass.Budget.FyrdBlocked);
            Assert.Equal(110m, pass.Budget.FyrdCostToday);
            Assert.Equal("fyrd", pass.Views[Peasant].Blocked);
            Assert.Equal("fyrd", pass.Views[Other].Blocked);
            Assert.Null(pass.Views[Ctl].Blocked);
        }
        Assert.Empty(_cloud.Interrupted);
        var sent = Assert.Single(_owner.Sent);
        Assert.Contains("the fyrd reached its 100.00 USD daily limit (110.00 USD spent)", sent.Text);

        // Raised and crossed again: one more message.
        Limits(fyrd: 150);
        Assert.False((await Run(true, Peasant, Other, Ctl)).Budget.FyrdBlocked);
        Spend(Ctl, 50);
        await Run(true, Peasant, Other, Ctl);
        Assert.Equal(2, _owner.Sent.Count);
    }

    [Fact]
    public async Task FyrdTotal_CountsSessionsArchivedToday()
    {
        Limits(fyrd: 100);
        _ledger.Record("archived", "gone", 70);
        Spend(Peasant, 31);
        var pass = await Run(false, Peasant);
        Assert.Equal(101m, pass.Budget.FyrdCostToday);
        Assert.True(pass.Budget.FyrdBlocked);
    }

    [Fact]
    public async Task SessionBlock_WinsOverFyrdBlock()
    {
        Limits(session: 10, fyrd: 20);
        Spend(Peasant, 15);
        Spend(Other, 8);
        var pass = await Run(false, Peasant, Other);
        Assert.Equal("session", pass.Views[Peasant].Blocked);
        Assert.Equal("fyrd", pass.Views[Other].Blocked);
        Assert.Equal([Peasant], _cloud.Interrupted);
    }

    [Fact]
    public async Task NoController_InterruptsButSendsNothing()
    {
        Spend(Peasant, 60);
        await Run(false, Peasant);
        Assert.Equal([Peasant], _cloud.Interrupted);
        Assert.Empty(_owner.Sent);
        Assert.Equal(50m, _ledger.NotifiedAtLimit(Peasant));
    }

    [Fact]
    public async Task Midnight_ResetsRaisesAndMarkers()
    {
        await CreateController();
        Spend(Peasant, 60);
        _ledger.AddExtra(Peasant, 25);
        Spend(Peasant, 20); // 80 > 75
        await Run(true, Peasant, Ctl);
        Assert.Equal(75m, _ledger.NotifiedAtLimit(Peasant));

        _time.Advance(TimeSpan.FromHours(13)); // next UTC day
        var pass = await Run(true, Peasant, Ctl);
        Assert.Equal(50m, pass.Views[Peasant].LimitUsd);
        Assert.Equal(0m, pass.Views[Peasant].CostToday);
        Assert.Null(pass.Views[Peasant].Blocked);
        Assert.Null(_ledger.NotifiedAtLimit(Peasant));
        Assert.Equal("2026-11-02", pass.Budget.Date);
    }

    [Fact]
    public async Task SessionWithoutCostData_HasNoView_AndIsNotBlocked()
    {
        var pass = await Run(false, Peasant);
        Assert.False(pass.Views.ContainsKey(Peasant));
        Assert.False(pass.Budget.FyrdBlocked);
    }
}

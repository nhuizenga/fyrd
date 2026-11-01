using System.Text.Json;
using Fyrd.Core.CloudSessions;
using Fyrd.Core.Config;
using Fyrd.Core.Hub;

namespace Fyrd.Core.Tests;

public sealed class CostLedgerTests : IDisposable
{
    private readonly TempHome _home = new();
    private readonly FixedTime _time = new(DateTimeOffset.Parse("2026-11-01T12:00:00Z"));

    public void Dispose() => _home.Dispose();

    private ConfigStore Config(string? tz = null)
    {
        var c = new ConfigStore(_home.Paths);
        c.Save(c.Load() with { TimeZone = tz });
        return c;
    }

    private CostLedger Make(string? tz = "UTC") => new(_home.Paths, Config(tz), _time);

    private static SessionEvent Result(string uuid, string at, string raw)
    {
        var json = JsonDocument.Parse(raw).RootElement.Clone();
        return new SessionEvent(uuid, "result", DateTimeOffset.Parse(at), null, json);
    }

    private static SessionEvent Turn(string uuid, string at, decimal cost) =>
        Result(uuid, at, $"{{\"model_usage_since_last_result\":{{\"m\":{{\"costUSD\":{cost}}}}}}}");

    private static JsonElement Usage(decimal cost) =>
        JsonDocument.Parse($"{{\"external_metadata\":{{\"usage\":{{\"cost_usd\":{cost}}}}}}}").RootElement.Clone();

    [Fact]
    public void Feed_FixtureResult_SumsOverModels()
    {
        var ledger = Make();
        ledger.Feed("s1", FixtureEvents.Load("result-event.json"));
        var c = ledger.TodayCost("s1");
        Assert.Equal(0.32m, c.Cost);
        Assert.False(c.Estimated);
    }

    [Fact]
    public void Feed_FallsBackToTotalDelta_WhenPerTurnMapMissing()
    {
        var ledger = Make();
        ledger.Feed("s1", [Result("a", "2026-11-01T01:00:00Z", "{\"total_cost_usd\":1.0}"),
                           Result("b", "2026-11-01T02:00:00Z", "{\"total_cost_usd\":1.75}"),
                           Result("c", "2026-11-01T03:00:00Z", "{\"total_cost_usd\":0.5}")]); // worker restarted: total reset
        Assert.Equal(2.25m, ledger.TodayCost("s1").Cost);
    }

    [Fact]
    public void Feed_FindsFieldsInWrapper()
    {
        var ledger = Make();
        ledger.Feed("s1", [Result("a", "2026-11-01T01:00:00Z",
            "{\"internal_anthropic_catchall\":{\"model_usage_since_last_result\":{\"m\":{\"costUSD\":2}}}}")]);
        Assert.Equal(2m, ledger.TodayCost("s1").Cost);
    }

    [Fact]
    public void Feed_SameResultTwice_CountedOnce()
    {
        var ledger = Make();
        var e = Turn("a", "2026-11-01T01:00:00Z", 1m);
        ledger.Feed("s1", [e]);
        ledger.Feed("s1", [e]);
        Assert.Equal(1m, ledger.TodayCost("s1").Cost);
    }

    [Theory]
    [InlineData("America/Los_Angeles", "2026-11-01T06:59:00Z", "2026-11-01T07:01:00Z")] // 23:59 Oct 31 / 00:01 Nov 1 PDT
    [InlineData("UTC", "2026-11-01T23:59:00Z", "2026-11-02T00:01:00Z")]
    public void Feed_MidnightSplitsDays(string tz, string before, string after)
    {
        var ledger = Make(tz);
        ledger.Feed("s1", [Turn("a", before, 3m), Turn("b", after, 5m)]);
        _time.Advance(DateTimeOffset.Parse(after) - _time.GetUtcNow());
        Assert.Equal(5m, ledger.TodayCost("s1").Cost); // only the turn after local midnight is today
        _time.Advance(TimeSpan.FromDays(-1));
        Assert.Equal(3m, ledger.TodayCost("s1").Cost);
    }

    [Fact]
    public void InFlight_IsCumulativeMinusResults_NeverNegative()
    {
        var ledger = Make();
        ledger.Feed("s1", [Turn("a", "2026-11-01T01:00:00Z", 2m)]);
        ledger.Observe("s1", Usage(2.75m));
        var c = ledger.TodayCost("s1");
        Assert.Equal(2.75m, c.Cost);
        Assert.True(c.Estimated);

        ledger.Observe("s1", Usage(1m)); // cumulative behind the results (stale listing)
        c = ledger.TodayCost("s1");
        Assert.Equal(2m, c.Cost);
        Assert.False(c.Estimated);
    }

    [Fact]
    public void NoSource_HasNoData()
    {
        var ledger = Make();
        Assert.False(ledger.TodayCost("s1").HasData);
        ledger.Observe("s1", JsonDocument.Parse("{}").RootElement);
        Assert.False(ledger.TodayCost("s1").HasData);
    }

    [Fact]
    public void Restart_FullReRead_RebuildsSameTotal()
    {
        var events = new[] { Turn("a", "2026-11-01T01:00:00Z", 1m), Turn("b", "2026-11-01T02:00:00Z", 2m) };
        var first = Make();
        first.Feed("s1", events);
        first.Observe("s1", Usage(3.5m));
        var before = first.TodayCost("s1");

        var second = Make(); // new process
        second.Feed("s1", events);
        second.Observe("s1", Usage(3.5m));
        Assert.Equal(before, second.TodayCost("s1"));
    }

    [Fact]
    public void CostsFile_KeepsArchivedSpendToday_AndDropsYesterday()
    {
        var ledger = Make();
        ledger.Record("old", "gone", 7m);
        ledger.AddExtra("old", 25m);
        Assert.Equal(7m, Make().ArchivedToday(new HashSet<string>()));
        Assert.Equal(7m, Make().TodayCost("old").Cost);
        Assert.Equal(25m, Make().ExtraUsd("old"));
        Assert.Equal(0m, Make().ArchivedToday(new HashSet<string> { "old" }));

        _time.Advance(TimeSpan.FromDays(1));
        var next = Make();
        Assert.Equal(0m, next.ArchivedToday(new HashSet<string>()));
        Assert.Equal(0m, next.ExtraUsd("old"));
        Assert.Null(next.NotifiedAtLimit("old"));
    }

    [Fact]
    public void Markers_Persist()
    {
        var ledger = Make();
        ledger.SetNotified("s1", 50m);
        ledger.SetFyrdNotified(500m);
        var again = Make();
        Assert.Equal(50m, again.NotifiedAtLimit("s1"));
        Assert.Equal(500m, again.FyrdNotifiedAtLimit);
    }

    [Fact]
    public void UnknownTimeZone_FallsBackToLocal()
    {
        File.WriteAllText(_home.Paths.ConfigFile, "{\"timeZone\":\"Mars/Olympus\"}");
        var ledger = new CostLedger(_home.Paths, new ConfigStore(_home.Paths), _time);
        Assert.Equal(TimeZoneInfo.Local.Id, ledger.TimeZone.Id);
    }
}

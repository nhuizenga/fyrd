using System.Diagnostics;
using Fyrd.Core.CloudSessions;
using Fyrd.Core.Config;
using Fyrd.Core.Hub;
using Xunit.Abstractions;

namespace Fyrd.Core.Tests;

/// <summary>
/// Benchmark for the first poll after a start, when every session's whole history is read: a long list must show up
/// early and the history reads must overlap instead of running one session after another.
/// </summary>
public sealed class HubRunnerFirstLoadTests(ITestOutputHelper output) : IDisposable
{
    private const int Sessions = 60;
    private const int EventsPerSession = 200;
    private const int PageSize = 50; // 4 pages per session
    private static readonly TimeSpan Latency = TimeSpan.FromMilliseconds(20);

    private readonly TempHome _home = new();
    private readonly string _seeds = Path.Combine(Path.GetTempPath(), "fyrd-seeds-" + Guid.NewGuid());

    public void Dispose()
    {
        _home.Dispose();
        if (Directory.Exists(_seeds)) Directory.Delete(_seeds, true);
    }

    [Fact]
    public async Task FirstPoll_ReadsHistoriesConcurrentlyAndPublishesTheListEarly()
    {
        Directory.CreateDirectory(_seeds);
        File.WriteAllText(Path.Combine(_seeds, "controller.md"), "---\nname: Controller\nmodel: m\nlocked: true\n---\nx");
        var cloud = new FakeCloudSessions { PageSize = PageSize };
        var sessions = Enumerable.Range(0, Sessions).Select(i =>
            new CloudSession($"s{i}", $"t{i}", "idle", "connected", null, ["fyrd-hub"], FakeCloudSessions.Empty)).ToArray();
        cloud.SetByTag("fyrd-hub", sessions);
        foreach (var s in sessions)
            for (var e = 0; e < EventsPerSession; e++)
                cloud.AddEvent(s.Id, new SessionEvent($"{s.Id}-{e}", "user", DateTimeOffset.UnixEpoch.AddSeconds(e), "hi", FakeCloudSessions.Empty));
        cloud.EventLatency = Latency;

        var owner = new FakeOwnerMessages();
        var time = new FixedTime(DateTimeOffset.Parse("2026-10-31T12:00:00Z"));
        var memory = new HubMemory(_home.Paths);
        var personas = new HubPersonas(_home.Paths, _seeds);
        var config = new ConfigStore(_home.Paths);
        config.Save(new FyrdConfig(EnvironmentId: "env_abc123"));
        var controller = new ControllerManager(cloud, owner, personas, memory, config, _home.Paths);
        var ledger = new CostLedger(_home.Paths, config, time);
        var guard = new CostGuard(ledger, config, cloud, owner, controller);
        var state = new HubState();
        var runner = new HubRunner(cloud, memory, personas, controller, state, _home.Paths, time, ledger, guard,
            new PendingMemory(_home.Paths, memory), owner);

        var clock = Stopwatch.StartNew();
        var poll = runner.PollOnceAsync(default);
        TimeSpan? listShown = null;
        while (!poll.IsCompleted)
        {
            if (listShown is null && state.Sessions.Count == Sessions) listShown = clock.Elapsed;
            await Task.Delay(1);
        }
        await poll;
        var total = clock.Elapsed;
        listShown ??= total;

        output.WriteLine($"sessions={Sessions} listEventCalls={cloud.ListEventCalls} maxConcurrent={cloud.MaxConcurrentListEvents} " +
                         $"listVisibleMs={listShown.Value.TotalMilliseconds:F0} firstPollMs={total.TotalMilliseconds:F0}");
        Assert.Equal(Sessions * (EventsPerSession / PageSize), cloud.ListEventCalls);
        // Sequentially this is Sessions * pages * latency = 4.8 s; the list must not wait for it.
        Assert.True(listShown.Value < TimeSpan.FromMilliseconds(500), $"list visible after {listShown.Value.TotalMilliseconds:F0} ms");
        Assert.True(total < TimeSpan.FromSeconds(2), $"first poll took {total.TotalMilliseconds:F0} ms");
        Assert.True(cloud.MaxConcurrentListEvents > 1);
    }
}

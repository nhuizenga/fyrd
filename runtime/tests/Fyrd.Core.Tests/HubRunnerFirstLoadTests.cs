using System.Diagnostics;
using System.Text.Json;
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

    private sealed record Rig(FakeCloudSessions Cloud, HubState State, HubRunner Runner, CloudSession[] Sessions);

    private Rig Build(int sessions, int events, TimeSpan latency, IGitHub? gh = null, Func<int, string>? raw = null)
    {
        Directory.CreateDirectory(_seeds);
        PersonaSeedFile.Write(_seeds, "controller", "Controller", "m", true, "x");
        var cloud = new FakeCloudSessions { PageSize = PageSize };
        var list = Enumerable.Range(0, sessions).Select(i =>
            new CloudSession($"s{i}", $"t{i}", "idle", "connected", null, ["fyrd-hub"],
                raw is null ? FakeCloudSessions.Empty : JsonDocument.Parse(raw(i)).RootElement.Clone())).ToArray();
        cloud.SetByTag("fyrd-hub", list);
        foreach (var s in list)
            for (var e = 0; e < events; e++)
                cloud.AddEvent(s.Id, new SessionEvent($"{s.Id}-{e}", "user", DateTimeOffset.UnixEpoch.AddSeconds(e + 1), "hi", FakeCloudSessions.Empty));
        cloud.EventLatency = latency;

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
            new PendingMemory(_home.Paths, memory), owner, gh is null ? null : new OwnedPullRequests(gh, time));
        return new Rig(cloud, state, runner, list);
    }

    [Fact]
    public async Task FirstPoll_ReadsHistoriesConcurrentlyAndPublishesTheListEarly()
    {
        var rig = Build(Sessions, EventsPerSession, Latency);
        var (cloud, state) = (rig.Cloud, rig.State);
        // Hold every history read until the list has been observed, so "the list is visible before any history is read" is checked at quiescence, not by racing the poll.
        cloud.EventGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var clock = Stopwatch.StartNew();
        var poll = rig.Runner.PollOnceAsync(default);
        await WaitUntil(() => state.Sessions.Count == Sessions, "the session list to be published");
        var listShown = clock.Elapsed;
        var firstSnapshot = state.Sessions;
        Assert.False(poll.IsCompleted);
        Assert.All(firstSnapshot, v => Assert.Null(v.LastActivity));

        cloud.EventGate.SetResult();
        await poll;
        var total = clock.Elapsed;

        output.WriteLine($"sessions={Sessions} listEventCalls={cloud.ListEventCalls} maxConcurrent={cloud.MaxConcurrentListEvents} " +
                         $"listVisibleMs={listShown.TotalMilliseconds:F0} firstPollMs={total.TotalMilliseconds:F0}");
        Assert.Equal(Sessions * (EventsPerSession / PageSize), cloud.ListEventCalls);
        Assert.True(cloud.MaxConcurrentListEvents > 1);
        Assert.All(state.Sessions, v => Assert.NotNull(v.LastActivity));
    }

    [Fact]
    public async Task FirstPoll_PublishesRowsProgressivelyBeforeThePassEnds()
    {
        var rig = Build(Sessions, EventsPerSession, TimeSpan.Zero);
        // Hold the reads of the last sessions so the pass cannot finish; the first ones fill in and are published meanwhile.
        rig.Cloud.EventGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Cloud.EventGateWhen = id => int.Parse(id[1..]) >= 30;

        var poll = rig.Runner.PollOnceAsync(default);
        await WaitUntil(() => rig.State.Sessions.Count(v => v.LastActivity is not null) >= 10, "the first rows to be published");
        Assert.False(poll.IsCompleted);
        Assert.Equal(Sessions, rig.State.Sessions.Count);
        Assert.Contains(rig.State.Sessions, v => v.LastActivity is null); // mixed: filled rows and bare rows

        rig.Cloud.EventGate.SetResult();
        await poll;
        Assert.All(rig.State.Sessions, v => Assert.NotNull(v.LastActivity));
    }

    [Fact]
    public async Task FirstPoll_ListsEachReposPullRequestsOnceAndConcurrently()
    {
        // 30 sessions over 6 repos; each GitHub listing takes 100 ms. Serially that is 600 ms for listings alone.
        var gh = new LatentGitHub(TimeSpan.FromMilliseconds(100));
        var rig = Build(30, 4, TimeSpan.Zero, gh, i =>
            "{\"session_context\":{\"sources\":[{\"url\":\"https://github.com/acme/repo" + i % 6 + "\"}]},\"external_metadata\":{\"current_branches\":{\"x\":\"b" + i + "\"}}}");

        var clock = Stopwatch.StartNew();
        await rig.Runner.PollOnceAsync(default);
        var total = clock.Elapsed;

        output.WriteLine($"githubListings={gh.Listings} maxConcurrent={gh.MaxConcurrent} pollMs={total.TotalMilliseconds:F0}");
        Assert.Equal(6, gh.Listings);
        Assert.True(gh.MaxConcurrent > 1, "listings did not overlap");
        Assert.Equal(30, rig.State.Sessions.Count);
    }

    [Fact]
    public async Task OneSessionsReadFailing_KeepsTheOthersAndReportsTheError()
    {
        var rig = Build(12, 4, TimeSpan.FromMilliseconds(5));
        rig.Cloud.ThrowOnListEventsFor("s5", new InvalidOperationException("boom"));

        await rig.Runner.PollOnceAsync(default);

        Assert.Equal(12, rig.State.Sessions.Count);
        Assert.Contains("boom", rig.State.LastError);
        Assert.Equal(11, rig.State.Sessions.Count(v => v.LastActivity is not null));
        Assert.False(rig.State.SignedOut);
    }

    /// <summary>Polls a condition instead of sleeping a fixed time; the generous timeout only bounds a hang.</summary>
    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("timed out waiting for " + what);
            await Task.Delay(2);
        }
    }

    [Fact]
    public async Task AuthErrorFromAConcurrentRead_SignsOutAndStopsReading()
    {
        var rig = Build(40, 4, TimeSpan.FromMilliseconds(20));
        rig.Cloud.ThrowOnListEventsFor("s0", new CloudSessionAuthException(401));

        await rig.Runner.PollOnceAsync(default);
        await WaitUntil(() => rig.Cloud.InFlightListEvents == 0, "in-flight reads to drain");
        var calls = rig.Cloud.ListEventCalls;
        await Task.Delay(50); // settle: a read started after the abort would be a bug; this can only hide it, never cause a failure

        Assert.True(rig.State.SignedOut);
        Assert.Equal(calls, rig.Cloud.ListEventCalls); // no reads left running after the pass aborted
        Assert.True(calls < 40, $"{calls} reads were made after the first one failed");
    }

    [Fact]
    public async Task CancelledMidPass_CancelsTheReadsInFlight()
    {
        var rig = Build(60, 4, TimeSpan.FromMilliseconds(50));
        using var cts = new CancellationTokenSource();

        var poll = rig.Runner.PollOnceAsync(cts.Token);
        await WaitUntil(() => rig.Cloud.ListEventCalls >= 1, "the first read to start");
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => poll);
        await WaitUntil(() => rig.Cloud.InFlightListEvents == 0, "in-flight reads to drain");
        var calls = rig.Cloud.ListEventCalls;
        await Task.Delay(50); // settle (see above)

        Assert.Equal(calls, rig.Cloud.ListEventCalls);
        Assert.True(calls < 60 * 1);
    }

    [Fact]
    public async Task ReadAhead_IsBounded()
    {
        // Reads block on a gate and 60 sessions are listed: reads must not all be started before the first is consumed.
        // With the gate closed nothing completes, so the number of started reads is observed at quiescence, not after a sleep.
        var rig = Build(60, 4, TimeSpan.Zero);
        rig.Cloud.EventGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var poll = rig.Runner.PollOnceAsync(cts.Token);
        await WaitUntil(() => rig.Cloud.ListEventCalls >= 8, "the first reads to start");
        await Task.Delay(50); // settle: more reads starting than the bound allows would be a bug
        var started = rig.Cloud.ListEventCalls;
        var maxConcurrent = rig.Cloud.MaxConcurrentListEvents;
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => poll);

        Assert.True(started <= 16 + 8, $"{started} reads started while none had completed");
        Assert.True(maxConcurrent <= 8, $"{maxConcurrent} reads were in flight at once");
    }

    /// <summary>A GitHub fake with per-listing latency and concurrency counters.</summary>
    private sealed class LatentGitHub(TimeSpan latency) : IGitHub
    {
        private int _listings, _inFlight, _max;
        public int Listings => _listings;
        public int MaxConcurrent => _max;
        public bool Configured => true;
        public Task<string?> LoginAsync(CancellationToken ct = default) => Task.FromResult<string?>("me");

        public async Task<PullRequestListing> ListAsync(string repo, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _listings);
            var now = Interlocked.Increment(ref _inFlight);
            int seen;
            while ((seen = _max) < now && Interlocked.CompareExchange(ref _max, now, seen) != seen) { }
            try { await Task.Delay(latency, ct); }
            finally { Interlocked.Decrement(ref _inFlight); }
            return new PullRequestListing("main", []);
        }

        public Task<PullRequestStatus> GetAsync(string url, CancellationToken ct = default) => throw new NotSupportedException();
        public Task MarkReadyAsync(string url, CancellationToken ct = default) => throw new NotSupportedException();
        public Task MergeAsync(string url, string? headSha = null, CancellationToken ct = default) => throw new NotSupportedException();
    }
}

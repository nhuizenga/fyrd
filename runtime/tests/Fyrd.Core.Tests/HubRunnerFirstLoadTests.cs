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
        File.WriteAllText(Path.Combine(_seeds, "controller.md"), "---\nname: Controller\nmodel: m\nlocked: true\n---\nx");
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

        var clock = Stopwatch.StartNew();
        var poll = rig.Runner.PollOnceAsync(default);
        TimeSpan? listShown = null;
        IReadOnlyList<HubSessionView>? firstSnapshot = null;
        var progressive = false;
        while (!poll.IsCompleted)
        {
            var snap = state.Sessions;
            if (listShown is null && snap.Count == Sessions && !poll.IsCompleted)
            {
                listShown = clock.Elapsed;
                firstSnapshot = snap;
            }
            // Rows fill in before the pass ends: some have activity while others are still bare.
            if (snap.Count == Sessions && snap.Any(v => v.LastActivity is not null) && snap.Any(v => v.LastActivity is null) && !poll.IsCompleted)
                progressive = true;
            await Task.Delay(1);
        }
        await poll;
        var total = clock.Elapsed;

        output.WriteLine($"sessions={Sessions} listEventCalls={cloud.ListEventCalls} maxConcurrent={cloud.MaxConcurrentListEvents} " +
                         $"listVisibleMs={listShown?.TotalMilliseconds:F0} firstPollMs={total.TotalMilliseconds:F0}");
        Assert.Equal(Sessions * (EventsPerSession / PageSize), cloud.ListEventCalls);
        // The full list was observed while the poll was still running, as bare rows. Timings are only printed: wall-clock bounds are flaky under load.
        Assert.NotNull(listShown);
        Assert.All(firstSnapshot!, v => Assert.Null(v.LastActivity));
        Assert.True(progressive, "no snapshot mixed filled and bare rows before the pass ended");
        Assert.True(cloud.MaxConcurrentListEvents > 1);
        Assert.All(state.Sessions, v => Assert.NotNull(v.LastActivity));
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

    [Fact]
    public async Task AuthErrorFromAConcurrentRead_SignsOutAndStopsReading()
    {
        var rig = Build(40, 4, TimeSpan.FromMilliseconds(20));
        rig.Cloud.ThrowOnListEventsFor("s0", new CloudSessionAuthException(401));

        await rig.Runner.PollOnceAsync(default);
        var calls = rig.Cloud.ListEventCalls;
        await Task.Delay(300);

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
        await Task.Delay(120);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => poll);
        var calls = rig.Cloud.ListEventCalls;
        await Task.Delay(300);

        Assert.Equal(calls, rig.Cloud.ListEventCalls);
        Assert.True(calls < 60 * 1);
    }

    [Fact]
    public async Task ReadAhead_IsBounded()
    {
        // Each read is slow and 60 sessions are listed: reads must not all be started before the first is consumed.
        var rig = Build(60, 4, TimeSpan.FromMilliseconds(40));
        using var cts = new CancellationTokenSource();
        var poll = rig.Runner.PollOnceAsync(cts.Token);
        await Task.Delay(100);
        var started = rig.Cloud.ListEventCalls;
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => poll);

        Assert.True(started <= 16 + 8, $"{started} reads started within 100 ms");
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

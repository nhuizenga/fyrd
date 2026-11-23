using Fyrd.Core.CloudSessions;
using Fyrd.Core.Hub;

namespace Fyrd.Core.Tests;

public sealed class SessionPullRequestsTests : IDisposable
{
    private readonly TempHome _home = new();
    private readonly FixedTime _time = new(DateTimeOffset.Parse("2026-11-20T12:00:00Z"));
    private readonly FakeCloudSessions _cloud = new();

    public void Dispose() => _home.Dispose();

    private SessionPullRequests Make() => new(_home.Paths, _time);

    private string File_ => Path.Combine(_home.Paths.HubDir, "session-prs.json");

    private const string Pr1 = "https://github.com/acme/widgets/pull/1", Pr2 = "https://github.com/acme/widgets/pull/2";

    private static readonly IReadOnlySet<string> NoneLive = new HashSet<string>();

    [Fact]
    public void Add_Persists_AndAFreshStoreReadsItBack()
    {
        var store = Make();
        Assert.Equal([Pr1, Pr2], store.Add("s1", [Pr1, Pr2], SessionPullRequests.FromBlock));
        Assert.True(File.Exists(File_));

        var reloaded = Make().For("s1");
        Assert.Equal([Pr1, Pr2], reloaded.Select(p => p.Url));
        Assert.All(reloaded, p =>
        {
            Assert.Equal(SessionPullRequests.FromBlock, p.Source);
            Assert.Equal(_time.GetUtcNow(), p.FirstSeen);
        });
        Assert.Equal(["s1"], Make().SessionIds());
    }

    [Fact]
    public void Add_IsAddOnly_AndKeepsTheFirstSeenTimeAndSource()
    {
        var store = Make();
        store.Add("s1", [Pr1], SessionPullRequests.FromBranch);
        _time.Advance(TimeSpan.FromDays(2));

        Assert.Empty(store.Add("s1", [Pr1, "https://github.com/ACME/Widgets/pull/1"], SessionPullRequests.FromBlock));
        Assert.Equal([Pr2], store.Add("s1", [Pr2], SessionPullRequests.FromBlock));

        var list = Make().For("s1");
        Assert.Equal([Pr1, Pr2], list.Select(p => p.Url));
        Assert.Equal(SessionPullRequests.FromBranch, list[0].Source);
        Assert.Equal(DateTimeOffset.Parse("2026-11-20T12:00:00Z"), list[0].FirstSeen);
        Assert.Equal(SessionPullRequests.FromBlock, list[1].Source);
    }

    [Fact]
    public void Add_SkipsInvalidUrls_AndRefusesAnUnknownSource()
    {
        var store = Make();
        Assert.Empty(store.Add("s1", ["https://evil.example/pull/1", "", "https://github.com/acme/widgets/pull/1/files"], SessionPullRequests.FromBlock));
        Assert.Empty(store.SessionIds());
        Assert.False(File.Exists(File_));
        Assert.Throws<ArgumentException>(() => store.Add("s1", [Pr1], "whatever"));
    }

    [Fact]
    public void Add_CapsBlockUrlsPerSession_ButNotBranchMatches()
    {
        var store = Make();
        var many = Enumerable.Range(1, SessionPullRequests.MaxBlockPerSession + 10).Select(n => $"https://github.com/acme/widgets/pull/{n}").ToList();
        Assert.Equal(SessionPullRequests.MaxBlockPerSession, store.Add("s1", many, SessionPullRequests.FromBlock).Count);
        Assert.Empty(store.Add("s1", ["https://github.com/acme/widgets/pull/999"], SessionPullRequests.FromBlock));
        Assert.Single(store.Add("s1", ["https://github.com/acme/widgets/pull/999"], SessionPullRequests.FromBranch));
        // Another session has its own cap.
        Assert.Single(store.Add("s2", ["https://github.com/acme/widgets/pull/999"], SessionPullRequests.FromBlock));
    }

    [Fact]
    public void ACorruptFile_IsKeptAside_AndTheStoreStartsEmpty()
    {
        Directory.CreateDirectory(_home.Paths.HubDir);
        File.WriteAllText(File_, "{ not json");
        Assert.Empty(Make().For("s1"));
        Assert.True(File.Exists(File_ + ".corrupt"));
    }

    [Fact]
    public void HandEditedEntries_ThatTheStoreWouldRefuse_AreIgnored()
    {
        Directory.CreateDirectory(_home.Paths.HubDir);
        File.WriteAllText(File_, """
            {"sessions":{"s1":[{"url":"https://evil.example/x","firstSeen":"2026-11-01T00:00:00Z","source":"block"},
                               {"url":"https://github.com/acme/widgets/pull/1","firstSeen":"2026-11-01T00:00:00Z","source":"magic"},
                               {"url":"https://github.com/acme/widgets/pull/2","firstSeen":"2026-11-01T00:00:00Z","source":"branch"}]}}
            """);
        Assert.Equal([Pr2], Make().For("s1").Select(p => p.Url));
    }

    [Fact]
    public async Task Reap_DropsArchivedAndDeletedSessions_KeepsLiveAndUnknownOnes()
    {
        var store = Make();
        foreach (var id in new[] { "archived", "deleted", "alive", "flaky", "listed" }) store.Add(id, [Pr1], SessionPullRequests.FromBlock);
        _cloud.SetSession(FakeCloudSessions.Session("archived", "archived"));
        _cloud.SetSession(FakeCloudSessions.Session("alive", "idle"));
        _cloud.SetSession(FakeCloudSessions.Session("flaky", "idle"));
        _cloud.ThrowOnGetFor("flaky", new CloudSessionApiException(503, "busy"));
        // "deleted" is unknown to the fake: 404. "listed" is in the live listing, so it is not even asked about.
        _cloud.ThrowOnGetFor("listed", new InvalidOperationException("must not be asked"));

        var dropped = await store.ReapAsync(_cloud, new HashSet<string> { "listed" });

        Assert.Equal(["archived", "deleted"], dropped);
        Assert.Equal(["alive", "flaky", "listed"], Make().SessionIds());
    }

    [Fact]
    public async Task Reap_KeepsEntries_OnANetworkErrorOrTimeout()
    {
        var store = Make();
        store.Add("s1", [Pr1], SessionPullRequests.FromBlock);
        store.Add("s2", [Pr1], SessionPullRequests.FromBlock);
        _cloud.ThrowOnGetFor("s1", new HttpRequestException("reset"));
        _cloud.ThrowOnGetFor("s2", new TaskCanceledException("timeout"));

        Assert.Empty(await store.ReapAsync(_cloud, NoneLive));
        Assert.Equal(["s1", "s2"], Make().SessionIds());
    }

    [Fact]
    public async Task Reap_StopsAndPropagates_OnAnAuthFailure_WithoutDroppingAnything()
    {
        var store = Make();
        store.Add("a", [Pr1], SessionPullRequests.FromBlock);
        store.Add("b", [Pr1], SessionPullRequests.FromBlock);
        _cloud.ThrowOnGetFor("a", new CloudSessionAuthException(401));

        await Assert.ThrowsAsync<CloudSessionAuthException>(() => store.ReapAsync(_cloud, NoneLive));

        Assert.Equal(["a", "b"], Make().SessionIds());
        Assert.Null(store.ReapedAt);
    }

    [Fact]
    public async Task Reap_RunsAtMostOncePerInterval_AcrossRestarts()
    {
        var store = Make();
        store.Add("gone", [Pr1], SessionPullRequests.FromBlock);
        _cloud.SetSession(FakeCloudSessions.Session("gone", "idle"));
        Assert.Empty(await store.ReapAsync(_cloud, NoneLive));
        Assert.Equal(_time.GetUtcNow(), store.ReapedAt);

        _cloud.SetSession(FakeCloudSessions.Session("gone", "archived"));
        _time.Advance(TimeSpan.FromMinutes(59));
        Assert.Empty(await Make().ReapAsync(_cloud, NoneLive)); // a restart doesn't reset the interval

        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(["gone"], await Make().ReapAsync(_cloud, NoneLive));
        Assert.Empty(Make().SessionIds());
    }

    [Fact]
    public async Task Reap_WithNothingCached_AsksNothing_AndWritesNothing()
    {
        _cloud.ThrowOnNextCall(new InvalidOperationException("must not be asked"));
        Assert.Empty(await Make().ReapAsync(_cloud, NoneLive));
        Assert.False(File.Exists(File_));
    }
}

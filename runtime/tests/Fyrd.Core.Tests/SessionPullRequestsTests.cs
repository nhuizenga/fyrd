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
        Assert.Equal([Pr1, Pr2], store.Add("session_s1", [Pr1, Pr2], SessionPullRequests.FromBlock));
        Assert.True(File.Exists(File_));

        var reloaded = Make().For("session_s1");
        Assert.Equal([Pr1, Pr2], reloaded.Select(p => p.Url));
        Assert.All(reloaded, p =>
        {
            Assert.Equal(SessionPullRequests.FromBlock, p.Source);
            Assert.Equal(_time.GetUtcNow(), p.FirstSeen);
        });
        Assert.Equal(["session_s1"], Make().SessionIds());
    }

    [Fact]
    public void Add_IsAddOnly_AndKeepsTheFirstSeenTimeAndSource()
    {
        var store = Make();
        store.Add("session_s1", [Pr1], SessionPullRequests.FromBranch);
        _time.Advance(TimeSpan.FromDays(2));

        Assert.Empty(store.Add("session_s1", [Pr1, "https://github.com/ACME/Widgets/pull/1"], SessionPullRequests.FromBlock));
        Assert.Equal([Pr2], store.Add("session_s1", [Pr2], SessionPullRequests.FromBlock));

        var list = Make().For("session_s1");
        Assert.Equal([Pr1, Pr2], list.Select(p => p.Url));
        Assert.Equal(SessionPullRequests.FromBranch, list[0].Source);
        Assert.Equal(DateTimeOffset.Parse("2026-11-20T12:00:00Z"), list[0].FirstSeen);
        Assert.Equal(SessionPullRequests.FromBlock, list[1].Source);
    }

    [Fact]
    public void Add_SkipsInvalidUrls_AndRefusesAnUnknownSource()
    {
        var store = Make();
        Assert.Empty(store.Add("session_s1", ["https://evil.example/pull/1", "", "https://github.com/acme/widgets/pull/1/files"], SessionPullRequests.FromBlock));
        Assert.Empty(store.SessionIds());
        Assert.False(File.Exists(File_));
        Assert.Throws<ArgumentException>(() => store.Add("session_s1", [Pr1], "whatever"));
    }

    [Fact]
    public void Add_CapsBlockUrlsPerSession_ButNotBranchMatches()
    {
        var store = Make();
        var many = Enumerable.Range(1, SessionPullRequests.MaxBlockPerSession + 10).Select(n => $"https://github.com/acme/widgets/pull/{n}").ToList();
        Assert.Equal(SessionPullRequests.MaxBlockPerSession, store.Add("session_s1", many, SessionPullRequests.FromBlock).Count);
        Assert.Empty(store.Add("session_s1", ["https://github.com/acme/widgets/pull/999"], SessionPullRequests.FromBlock));
        Assert.Single(store.Add("session_s1", ["https://github.com/acme/widgets/pull/999"], SessionPullRequests.FromBranch));
        // Another session has its own cap.
        Assert.Single(store.Add("session_s2", ["https://github.com/acme/widgets/pull/999"], SessionPullRequests.FromBlock));
    }

    [Fact]
    public void ACorruptFile_IsKeptAside_AndTheStoreStartsEmpty()
    {
        Directory.CreateDirectory(_home.Paths.HubDir);
        File.WriteAllText(File_, "{ not json");
        Assert.Empty(Make().For("session_s1"));
        Assert.True(File.Exists(File_ + ".corrupt"));
    }

    [Fact]
    public void HandEditedEntries_ThatTheStoreWouldRefuse_AreIgnored()
    {
        Directory.CreateDirectory(_home.Paths.HubDir);
        File.WriteAllText(File_, """
            {"sessions":{"session_s1":[{"url":"https://evil.example/x","firstSeen":"2026-11-01T00:00:00Z","source":"block"},
                               {"url":"https://github.com/acme/widgets/pull/1","firstSeen":"2026-11-01T00:00:00Z","source":"magic"},
                               {"url":"https://github.com/acme/widgets/pull/99999999999","firstSeen":"2026-11-01T00:00:00Z","source":"block"},
                               {"url":"https://github.com/acme/../pull/3","firstSeen":"2026-11-01T00:00:00Z","source":"block"},
                               {"url":"https://github.com/acme/widgets/pull/02","firstSeen":"2026-11-01T00:00:00Z","source":"block"},
                               {"url":"https://github.com/-acme/widgets/pull/4","firstSeen":"2026-11-01T00:00:00Z","source":"block"},
                               {"url":"https://github.com/acme/widgets/pull/2","firstSeen":"2026-11-01T00:00:00Z","source":"branch"}]}}
            """);
        var store = Make();
        Assert.Equal([Pr2], store.For("session_s1").Select(p => p.Url));
        // Every cached URL parses: none can surface later as an OverflowException from a list, ready, merge or merge stack.
        Assert.All(store.For("session_s1"), p => PullRequestUrl.Parse(p.Url));
    }

    [Fact]
    public void Load_LowerCasesMixedCaseEntries_FromAnOlderFile_AndDedupesThem()
    {
        Directory.CreateDirectory(_home.Paths.HubDir);
        File.WriteAllText(File_, """
            {"sessions":{"session_s1":[{"url":"https://github.com/ACME/Widgets/pull/1","firstSeen":"2026-11-01T00:00:00Z","source":"branch"},
                               {"url":"https://github.com/acme/widgets/pull/1","firstSeen":"2026-11-02T00:00:00Z","source":"block"},
                               {"url":"https://github.com/Acme/widgets/pull/2","firstSeen":"2026-11-03T00:00:00Z","source":"stack"}]}}
            """);
        var store = Make();

        var list = store.For("session_s1");
        Assert.Equal([Pr1, Pr2], list.Select(p => p.Url));
        Assert.Equal((SessionPullRequests.FromBranch, DateTimeOffset.Parse("2026-11-01T00:00:00Z")), (list[0].Source, list[0].FirstSeen));
        Assert.Equal(SessionPullRequests.FromStack, list[1].Source);
        // A mixed-case spelling of a cached PR is the same PR, and is stored lower-cased.
        Assert.Empty(store.Add("session_s1", ["https://github.com/acme/WIDGETS/pull/2"], SessionPullRequests.FromBlock));
        Assert.Equal(["https://github.com/acme/widgets/pull/3"], store.Add("session_s1", ["https://github.com/ACME/widgets/pull/3"], SessionPullRequests.FromBlock));
    }

    [Fact]
    public void Add_RefusesTheUrlsLoadWouldRefuse()
    {
        var store = Make();
        Assert.Empty(store.Add("session_s1", [
            "https://github.com/acme/widgets/pull/99999999999",
            "https://github.com/acme/../pull/3",
            "https://github.com/acme/widgets/pull/01",
            "https://github.com/acme/widgets/pull/0",
        ], SessionPullRequests.FromBranch));
        Assert.Empty(store.SessionIds());
    }

    [Fact]
    public void HandEditedBlockEntries_PastTheCap_AreIgnored_ButBranchMatchesAreNot()
    {
        Directory.CreateDirectory(_home.Paths.HubDir);
        var entries = Enumerable.Range(1, SessionPullRequests.MaxBlockPerSession + 5)
            .Select(n => $$"""{"url":"https://github.com/acme/widgets/pull/{{n}}","firstSeen":"2026-11-01T00:00:00Z","source":"block"}""")
            .Append("""{"url":"https://github.com/acme/widgets/pull/999","firstSeen":"2026-11-01T00:00:00Z","source":"branch"}""");
        File.WriteAllText(File_, """{"sessions":{"session_s1":[""" + string.Join(",", entries) + "]}}");

        var list = Make().For("session_s1");
        Assert.Equal(SessionPullRequests.MaxBlockPerSession, list.Count(p => p.Source == SessionPullRequests.FromBlock));
        Assert.Equal("https://github.com/acme/widgets/pull/50", list.Last(p => p.Source == SessionPullRequests.FromBlock).Url);
        Assert.Contains(list, p => p.Url == "https://github.com/acme/widgets/pull/999");
    }

    [Fact]
    public async Task HandEditedSessionIds_TheCloudWouldRefuse_AreIgnored_AndNeverAddedOrAskedAbout()
    {
        Directory.CreateDirectory(_home.Paths.HubDir);
        File.WriteAllText(File_, """
            {"sessions":{"../etc":[{"url":"https://github.com/acme/widgets/pull/1","firstSeen":"2026-11-01T00:00:00Z","source":"block"}],
                         "s1":[{"url":"https://github.com/acme/widgets/pull/1","firstSeen":"2026-11-01T00:00:00Z","source":"block"}],
                         "session_ok":[{"url":"https://github.com/acme/widgets/pull/1","firstSeen":"2026-11-01T00:00:00Z","source":"block"}]}}
            """);
        var store = Make();
        Assert.Equal(["session_ok"], store.SessionIds());
        Assert.Empty(store.Add("session_bad-id", [Pr1], SessionPullRequests.FromBlock));
        Assert.Equal(["session_ok"], await store.ReapAsync(_cloud, NoneLive));
        Assert.Empty(Make().SessionIds());
    }

    [Fact]
    public async Task Reap_DropsArchivedAndDeletedSessions_KeepsLiveAndUnknownOnes()
    {
        var store = Make();
        foreach (var id in new[] { "session_archived", "session_deleted", "session_alive", "session_flaky", "session_listed" }) store.Add(id, [Pr1], SessionPullRequests.FromBlock);
        _cloud.SetSession(FakeCloudSessions.Session("session_archived", "archived"));
        _cloud.SetSession(FakeCloudSessions.Session("session_alive", "idle"));
        _cloud.SetSession(FakeCloudSessions.Session("session_flaky", "idle"));
        _cloud.ThrowOnGetFor("session_flaky", new CloudSessionApiException(503, "busy"));
        // "session_deleted" is unknown to the fake: 404. "session_listed" is in the live listing, so it is not even asked about.
        _cloud.ThrowOnGetFor("session_listed", new InvalidOperationException("must not be asked"));

        var dropped = await store.ReapAsync(_cloud, new HashSet<string> { "session_listed" });

        Assert.Equal(["session_archived", "session_deleted"], dropped);
        Assert.Equal(["session_alive", "session_flaky", "session_listed"], Make().SessionIds());
    }

    [Fact]
    public async Task Reap_KeepsEntries_OnANetworkErrorOrTimeout()
    {
        var store = Make();
        store.Add("session_s1", [Pr1], SessionPullRequests.FromBlock);
        store.Add("session_s2", [Pr1], SessionPullRequests.FromBlock);
        _cloud.ThrowOnGetFor("session_s1", new HttpRequestException("reset"));
        _cloud.ThrowOnGetFor("session_s2", new TaskCanceledException("timeout"));

        Assert.Empty(await store.ReapAsync(_cloud, NoneLive));
        Assert.Equal(["session_s1", "session_s2"], Make().SessionIds());
    }

    [Fact]
    public async Task Reap_StopsAndPropagates_OnAnAuthFailure_WithoutDroppingAnything()
    {
        var store = Make();
        store.Add("session_a", [Pr1], SessionPullRequests.FromBlock);
        store.Add("session_b", [Pr1], SessionPullRequests.FromBlock);
        _cloud.ThrowOnGetFor("session_a", new CloudSessionAuthException(401));

        await Assert.ThrowsAsync<CloudSessionAuthException>(() => store.ReapAsync(_cloud, NoneLive));

        Assert.Equal(["session_a", "session_b"], Make().SessionIds());
        Assert.Null(store.ReapedAt);
    }

    [Fact]
    public async Task Reap_RunsAtMostOncePerInterval_AcrossRestarts()
    {
        var store = Make();
        store.Add("session_gone", [Pr1], SessionPullRequests.FromBlock);
        _cloud.SetSession(FakeCloudSessions.Session("session_gone", "idle"));
        Assert.Empty(await store.ReapAsync(_cloud, NoneLive));
        Assert.Equal(_time.GetUtcNow(), store.ReapedAt);

        _cloud.SetSession(FakeCloudSessions.Session("session_gone", "archived"));
        _time.Advance(TimeSpan.FromMinutes(59));
        Assert.Empty(await Make().ReapAsync(_cloud, NoneLive)); // a restart doesn't reset the interval

        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(["session_gone"], await Make().ReapAsync(_cloud, NoneLive));
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

using Fyrd.Core.Hub;

namespace Fyrd.Core.Tests;

public sealed class StubGitHub : IGitHub
{
    public Dictionary<string, List<PullRequestHead>> ByRepo { get; } = [];
    public List<string> Listed { get; } = [];
    public Exception? Fail { get; set; }
    public bool Configured { get; set; } = true;
    public Task<string?> LoginAsync(CancellationToken ct = default) => Task.FromResult<string?>(Configured ? "me" : null);

    public string? DefaultBranch { get; set; } = "main";

    public Task<PullRequestListing> ListAsync(string repo, CancellationToken ct = default)
    {
        Listed.Add(repo);
        if (Fail is not null) throw Fail;
        return Task.FromResult(new PullRequestListing(DefaultBranch, ByRepo.TryGetValue(repo, out var l) ? l : []));
    }

    public Dictionary<string, PullRequestStatus> Statuses { get; } = [];
    public List<string> Got { get; } = [];
    public Exception? GetFail { get; set; }

    public Task<PullRequestStatus> GetAsync(string url, CancellationToken ct = default)
    {
        Got.Add(url);
        if (GetFail is not null) throw GetFail;
        return Statuses.TryGetValue(url, out var s) ? Task.FromResult(s) : throw new NotSupportedException();
    }
    public Task MarkReadyAsync(string url, CancellationToken ct = default) => throw new NotSupportedException();
    public Task MergeAsync(string url, string? headSha = null, CancellationToken ct = default) => throw new NotSupportedException();
}

public sealed class OwnedPullRequestsTests
{
    private const string Repo = "acme/widgets";
    private static string Pr(int n) => $"https://github.com/acme/widgets/pull/{n}";

    private readonly StubGitHub _gh = new();
    private readonly FixedTime _time = new(DateTimeOffset.Parse("2026-11-01T12:00:00Z"));

    public OwnedPullRequestsTests() =>
        _gh.ByRepo[Repo] = [new(Pr(13), "fyrd/a"), new(Pr(12), "fyrd/a"), new(Pr(14), "fyrd/b")];

    [Fact]
    public async Task Find_ReturnsThePrsWhoseHeadIsTheBranch_InNumberOrder()
    {
        var owned = new OwnedPullRequests(_gh, _time);

        Assert.Equal([Pr(12), Pr(13)], await owned.FindAsync(Repo, ["fyrd/a"]));
        Assert.Equal([Pr(14)], await owned.FindAsync(Repo, ["fyrd/b"]));
        Assert.Empty(await owned.FindAsync(Repo, ["fyrd/none"]));
    }

    [Fact]
    public async Task Find_MatchesAnyOfSeveralBranches()
    {
        var owned = new OwnedPullRequests(_gh, _time);

        Assert.Equal([Pr(12), Pr(13), Pr(14)], await owned.FindAsync(Repo, ["fyrd/b", "fyrd/a", "fyrd/none"]));
        Assert.Empty(await owned.FindAsync(Repo, []));
    }

    private static PullRequestStatus Light(int n) => new(Pr(n), n, "t", "open", false, "none");
    private static PullRequestStatus Full(int n) => new(Pr(n), n, "t", "open", false, "failing") { Mergeable = "conflicting" };

    [Fact]
    public async Task Find_ReadsAnOwnedPartialPr_OnItsOwn_OncePerListing()
    {
        _gh.ByRepo[Repo] = [new(Pr(12), "fyrd/a") { State = "open", Status = Light(12), Partial = true },
                            new(Pr(20), "fyrd/z") { State = "open", Status = Light(20), Partial = true }];
        _gh.Statuses[Pr(12)] = Full(12);
        var owned = new OwnedPullRequests(_gh, _time);

        Assert.Equal([Pr(12)], await owned.FindAsync(Repo, ["fyrd/a"]));
        Assert.Equal("failing", owned.Status(Pr(12))!.Checks);
        Assert.Equal("conflicting", owned.Status(Pr(12))!.Mergeable);
        await owned.FindAsync(Repo, ["fyrd/a"]);
        Assert.Equal([Pr(12)], _gh.Got); // once per listing; the unowned partial PR is never read
        Assert.Equal("none", owned.Status(Pr(20))!.Checks);
    }

    [Fact]
    public async Task Find_AFailedPartialRead_KeepsThePartialStatus_UntilTheNextListing()
    {
        _gh.ByRepo[Repo] = [new(Pr(12), "fyrd/a") { State = "open", Status = Light(12), Partial = true }];
        _gh.GetFail = new GitHubException("boom");
        var owned = new OwnedPullRequests(_gh, _time);

        Assert.Equal([Pr(12)], await owned.FindAsync(Repo, ["fyrd/a"]));
        await owned.FindAsync(Repo, ["fyrd/a"]);
        Assert.Single(_gh.Got);
        Assert.Equal("none", owned.Status(Pr(12))!.Checks);

        _time.Advance(OwnedPullRequests.Ttl + TimeSpan.FromSeconds(1));
        await owned.FindAsync(Repo, ["fyrd/a"]);
        Assert.Equal(2, _gh.Got.Count);
    }

    [Fact]
    public async Task Find_ARateLimitOnAPartialRead_StopsTheReads_AndTheNextListing()
    {
        _gh.ByRepo[Repo] = [new(Pr(12), "fyrd/a") { State = "open", Status = Light(12), Partial = true },
                            new(Pr(13), "fyrd/a") { State = "open", Status = Light(13), Partial = true }];
        _gh.GetFail = new GitHubRateLimitException(_time.GetUtcNow().AddMinutes(10));
        var owned = new OwnedPullRequests(_gh, _time);

        await owned.FindAsync(Repo, ["fyrd/a"]);
        Assert.Single(_gh.Got);
        _time.Advance(OwnedPullRequests.Ttl + TimeSpan.FromSeconds(1));
        await owned.FindAsync(Repo, ["fyrd/a"]);
        Assert.Single(_gh.Listed);
    }

    [Fact]
    public async Task Find_ListsEachRepoOncePerTtl()
    {
        var owned = new OwnedPullRequests(_gh, _time);

        await owned.FindAsync(Repo, ["fyrd/a"]);
        await owned.FindAsync(Repo, ["fyrd/b"]);
        Assert.Single(_gh.Listed);

        _time.Advance(OwnedPullRequests.Ttl + TimeSpan.FromSeconds(1));
        await owned.FindAsync(Repo, ["fyrd/a"]);
        Assert.Equal(2, _gh.Listed.Count);
    }

    [Theory]
    [InlineData(null, "fyrd/a")]
    [InlineData(Repo, null)]
    [InlineData("not a repo", "fyrd/a")]
    [InlineData("--repo=x/y", "fyrd/a")]
    public async Task Find_WithoutAValidRepoOrBranch_ReturnsNothingAndNeverCallsGh(string? repo, string? branch)
    {
        var owned = new OwnedPullRequests(_gh, _time);

        Assert.Empty(await owned.FindAsync(repo, [branch]));
        Assert.Empty(_gh.Listed);
    }

    [Fact]
    public async Task Find_WhenGhFails_KeepsTheLastListAndWaitsATtlBeforeRetrying()
    {
        var owned = new OwnedPullRequests(_gh, _time);
        await owned.FindAsync(Repo, ["fyrd/a"]);
        _time.Advance(OwnedPullRequests.Ttl + TimeSpan.FromSeconds(1));
        _gh.Fail = new GitHubException("gh is not installed");

        Assert.Equal([Pr(12), Pr(13)], await owned.FindAsync(Repo, ["fyrd/a"]));
        await owned.FindAsync(Repo, ["fyrd/a"]);
        Assert.Equal(2, _gh.Listed.Count);
    }

    [Fact]
    public async Task Find_WhenGhFailsWithNoList_ReturnsNothing()
    {
        _gh.Fail = new GitHubException("gh is not installed");

        Assert.Empty(await new OwnedPullRequests(_gh, _time).FindAsync(Repo, ["fyrd/a"]));
    }

    [Fact]
    public async Task Find_WithoutAToken_ReturnsNothingAndNeverCallsGitHub()
    {
        _gh.Configured = false;

        Assert.Empty(await new OwnedPullRequests(_gh, _time).FindAsync(Repo, ["fyrd/a"]));
        Assert.Empty(_gh.Listed);
    }

    [Fact]
    public async Task Find_WhenRateLimited_KeepsTheLastListAndDoesNotRetryBeforeTheReset()
    {
        var owned = new OwnedPullRequests(_gh, _time);
        await owned.FindAsync(Repo, ["fyrd/a"]);
        var resetAt = _time.GetUtcNow().AddMinutes(30);
        _time.Advance(OwnedPullRequests.Ttl + TimeSpan.FromSeconds(1));
        _gh.Fail = new GitHubRateLimitException(resetAt);

        Assert.Equal([Pr(12), Pr(13)], await owned.FindAsync(Repo, ["fyrd/a"]));
        Assert.Equal(2, _gh.Listed.Count);

        // Many TTLs later, but still before the reset: no request.
        _time.Advance(OwnedPullRequests.Ttl * 3);
        Assert.Equal([Pr(12), Pr(13)], await owned.FindAsync(Repo, ["fyrd/a"]));
        Assert.Equal(2, _gh.Listed.Count);

        _time.Advance(TimeSpan.FromMinutes(30));
        _gh.Fail = null;
        await owned.FindAsync(Repo, ["fyrd/a"]);
        Assert.Equal(3, _gh.Listed.Count);
    }

    [Fact]
    public void Ttl_IsSixtySeconds() => Assert.Equal(TimeSpan.FromSeconds(60), OwnedPullRequests.Ttl);

    private static PullRequestHead Open(int n, string head, string @base, string checks = "passing") =>
        new(Pr(n), head)
        {
            Base = @base, State = "open",
            Status = new PullRequestStatus(Pr(n), n, $"PR {n}", "open", false, checks) { Base = @base, Head = head },
        };

    [Fact]
    public async Task Status_ReadsTheCachedSummaryOfAListedPr()
    {
        _gh.ByRepo[Repo] = [Open(5, "fyrd/a", "main", "failing")];
        var owned = new OwnedPullRequests(_gh, _time);

        Assert.Null(owned.Status(Pr(5)));
        await owned.FindAsync(Repo, ["fyrd/a"]);

        Assert.Equal("failing", owned.Status(Pr(5))!.Checks);
        Assert.Null(owned.Status(Pr(6)));
    }

    [Fact]
    public async Task Invalidate_MakesTheNextFindListAgain_BeforeTheTtl()
    {
        _gh.ByRepo[Repo] = [Open(5, "fyrd/a", "main")];
        var owned = new OwnedPullRequests(_gh, _time);
        await owned.FindAsync(Repo, ["fyrd/a"]);
        await owned.FindAsync(Repo, ["fyrd/a"]);
        Assert.Single(_gh.Listed);

        owned.Invalidate(Repo);
        await owned.FindAsync(Repo, ["fyrd/a"]);

        Assert.Equal(2, _gh.Listed.Count);
    }

    [Fact]
    public async Task Invalidate_ThenAFailedListing_KeepsTheLastGoodStatuses()
    {
        _gh.ByRepo[Repo] = [Open(5, "fyrd/a", "main", "passing")];
        var owned = new OwnedPullRequests(_gh, _time);
        await owned.FindAsync(Repo, ["fyrd/a"]);
        owned.Invalidate(Repo);
        _gh.Fail = new GitHubException("GitHub answered 502");

        Assert.Equal([Pr(5)], await owned.FindAsync(Repo, ["fyrd/a"]));
        Assert.Equal("passing", owned.Status(Pr(5))!.Checks);
    }

    [Fact]
    public async Task FindOwned_AddsTheStackedPrs_AndMarksThemStacked()
    {
        _gh.ByRepo[Repo] = [Open(11, "fyrd/a", "main"), Open(12, "fyrd/b", "fyrd/a"), Open(13, "fyrd/c", "fyrd/b")];
        var owned = new OwnedPullRequests(_gh, _time);

        var set = await owned.FindOwnedAsync(Repo, ["fyrd/b"]);

        Assert.Equal([Pr(11), Pr(12), Pr(13)], set.Urls);
        Assert.Equal([Pr(11), Pr(13)], set.Stacked);
        Assert.Equal([Pr(11), Pr(12), Pr(13)], await owned.FindAsync(Repo, ["fyrd/b"]));
    }
}

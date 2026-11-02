using Fyrd.Core.Hub;

namespace Fyrd.Core.Tests;

public sealed class StubGitHub : IGitHub
{
    public Dictionary<string, List<PullRequestHead>> ByRepo { get; } = [];
    public List<string> Listed { get; } = [];
    public Exception? Fail { get; set; }

    public Task<IReadOnlyList<PullRequestHead>> ListAsync(string repo, CancellationToken ct = default)
    {
        Listed.Add(repo);
        if (Fail is not null) throw Fail;
        return Task.FromResult<IReadOnlyList<PullRequestHead>>(ByRepo.TryGetValue(repo, out var l) ? l : []);
    }

    public Task<PullRequestStatus> GetAsync(string url, CancellationToken ct = default) => throw new NotSupportedException();
    public Task MarkReadyAsync(string url, CancellationToken ct = default) => throw new NotSupportedException();
    public Task MergeAsync(string url, CancellationToken ct = default) => throw new NotSupportedException();
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
}

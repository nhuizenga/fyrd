using Fyrd.Core.Hub;

namespace Fyrd.Core.Tests;

public class PullRequestStacksTests
{
    private static string Url(int n) => $"https://github.com/acme/widgets/pull/{n}";

    private static PullRequestHead Pr(int n, string head, string @base, string state = "open", bool fork = false) =>
        new(Url(n), head) { Base = @base, State = state, CrossRepository = fork };

    private static OwnedSet Owned(IEnumerable<string> branches, params PullRequestHead[] prs) =>
        PullRequestStacks.Owned(new PullRequestListing("main", prs), branches);

    [Fact]
    public void ADirectPrOnly()
    {
        var set = Owned(["a"], Pr(1, "a", "main"), Pr(2, "other", "main"));
        Assert.Equal([Url(1)], set.Urls);
        Assert.Empty(set.Stacked);
    }

    [Fact]
    public void DirectPrsOfAnyStateCount_AsToday()
    {
        var set = Owned(["a"], Pr(1, "a", "main", "merged"), Pr(2, "a", "main", "closed"), Pr(3, "a", "main"));
        Assert.Equal([Url(1), Url(2), Url(3)], set.Urls);
        Assert.Empty(set.Stacked);
    }

    [Fact]
    public void AParentAndAChildAreAdded()
    {
        var set = Owned(["b"], Pr(11, "a", "main"), Pr(12, "b", "a"), Pr(13, "c", "b"));
        Assert.Equal([Url(11), Url(12), Url(13)], set.Urls);
        Assert.Equal([Url(11), Url(13)], set.Stacked);
    }

    [Fact]
    public void AThreeDeepChainIsFollowedBothWays()
    {
        var prs = new[] { Pr(1, "a", "main"), Pr(2, "b", "a"), Pr(3, "c", "b"), Pr(4, "d", "c") };
        foreach (var branch in new[] { "a", "b", "c", "d" })
            Assert.Equal([Url(1), Url(2), Url(3), Url(4)], Owned([branch], prs).Urls);
        Assert.Equal([Url(2), Url(3), Url(4)], Owned(["a"], prs).Stacked);
        Assert.Equal([Url(1), Url(2), Url(3)], Owned(["d"], prs).Stacked);
    }

    [Fact]
    public void AForkAddsBothChildren()
    {
        var set = Owned(["a"], Pr(1, "a", "main"), Pr(2, "b", "a"), Pr(3, "c", "a"));
        Assert.Equal([Url(1), Url(2), Url(3)], set.Urls);
        Assert.Equal([Url(2), Url(3)], set.Stacked);
    }

    [Fact]
    public void AMergedParentIsNotFollowed()
    {
        var set = Owned(["b"], Pr(11, "a", "main", "merged"), Pr(12, "b", "a"));
        Assert.Equal([Url(12)], set.Urls);
    }

    [Fact]
    public void AMergedOrClosedChildIsNotFollowed()
    {
        var set = Owned(["a"], Pr(1, "a", "main"), Pr(2, "b", "a", "merged"), Pr(3, "c", "a", "closed"));
        Assert.Equal([Url(1)], set.Urls);
    }

    [Fact]
    public void ADirectMergedPrDoesNotStartAWalk()
    {
        var set = Owned(["a"], Pr(1, "a", "main", "merged"), Pr(2, "b", "a"));
        Assert.Equal([Url(1)], set.Urls);
    }

    [Fact]
    public void TwoPrsThatBothTargetMainAreNotLinked()
    {
        var set = Owned(["a"], Pr(1, "a", "main"), Pr(2, "b", "main"));
        Assert.Equal([Url(1)], set.Urls);
    }

    [Fact]
    public void NeverLinksThroughTheDefaultBranch()
    {
        // A PR whose head is the default branch (or targets it) must not join two unrelated stacks.
        var set = Owned(["a"], Pr(1, "a", "main"), Pr(2, "main", "dev"), Pr(3, "x", "main"));
        Assert.Equal([Url(1)], set.Urls);
    }

    [Fact]
    public void CrossRepoHeadsAreIgnored()
    {
        var set = Owned(["a"], Pr(1, "a", "main"), Pr(2, "b", "a", fork: true), Pr(3, "z", "main"));
        Assert.Equal([Url(1)], set.Urls);
    }

    [Fact]
    public void OnlyThePrsOfTheGivenListingAreEverAdded()
    {
        // Each repo has its own listing, so another repo's PR can never appear.
        var set = PullRequestStacks.Owned(new PullRequestListing("main", [Pr(1, "a", "main")]), ["a"]);
        Assert.All(set.Urls, u => Assert.StartsWith("https://github.com/acme/widgets/", u));
    }

    [Fact]
    public void AMultiBranchSessionOwnsEachBranchsStack()
    {
        var set = Owned(["a", "x"], Pr(1, "a", "main"), Pr(2, "b", "a"), Pr(5, "x", "main"), Pr(6, "y", "x"));
        Assert.Equal([Url(1), Url(2), Url(5), Url(6)], set.Urls);
        Assert.Equal([Url(2), Url(6)], set.Stacked);
    }

    [Fact]
    public void ADirectPrIsNeverMarkedStacked()
    {
        var set = Owned(["a", "b"], Pr(1, "a", "main"), Pr(2, "b", "a"));
        Assert.Equal([Url(1), Url(2)], set.Urls);
        Assert.Empty(set.Stacked);
    }

    private static PullRequestStatus St(int n, string head, string @base, string state = "open") =>
        new(Url(n), n, $"PR {n}", state, false, "passing") { Head = head, Base = @base };

    [Fact]
    public void Warning_NamesTheChildAndTheTopDownOrder()
    {
        var prs = new[] { St(11, "a", "main"), St(12, "b", "a"), St(13, "c", "b") };
        Assert.Equal(
            "#12 is stacked on this PR. Merge from the top of the stack down: merge #13, then #12, then this. Merging this first leaves #12 targeting a merged branch.",
            PullRequestStacks.MergeOrderWarning(prs, prs[0]));
    }

    [Fact]
    public void Warning_IsNullForThePrAtTheTop_AndForAPrWithNoStack()
    {
        var prs = new[] { St(11, "a", "main"), St(12, "b", "a"), St(20, "x", "main") };
        Assert.Null(PullRequestStacks.MergeOrderWarning(prs, prs[1]));
        Assert.Null(PullRequestStacks.MergeOrderWarning(prs, prs[2]));
    }

    [Fact]
    public void Warning_IgnoresMergedClosedChildren()
    {
        var prs = new[] { St(11, "a", "main"), St(12, "b", "a", "merged"), St(13, "c", "a", "closed") };
        Assert.Null(PullRequestStacks.MergeOrderWarning(prs, prs[0]));
    }

    [Fact]
    public void Above_ForkListsEveryChild_AndEveryDescendantTopDown()
    {
        var prs = new[] { St(1, "a", "main"), St(2, "b", "a"), St(3, "c", "a"), St(4, "d", "b") };
        var (children, topDown) = PullRequestStacks.Above(prs, prs[0]);
        Assert.Equal([2, 3], children.Select(c => c.Number));
        Assert.Equal([4, 2, 3], topDown.Select(c => c.Number));
        Assert.Equal("#2, #3 are stacked on this PR. Merge from the top of the stack down: merge #4, then #2, then #3, then this. Merging this first leaves #2, #3 targeting a merged branch.",
            PullRequestStacks.MergeOrderWarning(prs, prs[0]));
    }

    [Fact]
    public void Above_NeverLoopsOnACycle()
    {
        var prs = new[] { St(1, "a", "b"), St(2, "b", "a") };
        var (_, topDown) = PullRequestStacks.Above(prs, prs[0]);
        Assert.Equal([2], topDown.Select(c => c.Number));
    }

    private static PullRequestStatus In(string repo, int n, string head, string @base, bool fork = false, string def = "main") =>
        new($"https://github.com/{repo}/pull/{n}", n, $"PR {n}", "open", false, "passing")
        { Head = head, Base = @base, CrossRepository = fork, DefaultBranch = def };

    [Fact]
    public void Links_OnlyWithinOneRepo()
    {
        var prs = new[] { In("acme/a", 1, "feat", "main"), In("acme/b", 2, "other", "feat") };
        Assert.Null(PullRequestStacks.MergeOrderWarning(prs, prs[0]));
        Assert.Empty(PullRequestStacks.Above(prs, prs[0]).Children);
        Assert.False(PullRequestStacks.Stacked(prs[0], prs[1]));
    }

    [Fact]
    public void Links_ThroughAnIdenticalRepoNameInDifferentCase()
    {
        var prs = new[] { In("Acme/Widgets", 1, "feat", "main"), In("acme/widgets", 2, "other", "feat") };
        Assert.True(PullRequestStacks.Stacked(prs[0], prs[1]));
    }

    [Fact]
    public void AForkPrWhoseHeadIsTheDefaultBranch_NeverMakesEverythingStacked()
    {
        // Fork PR "main → release" must not make every PR that targets main look stacked on it.
        var prs = new[] { In("acme/a", 1, "main", "release", fork: true), In("acme/a", 2, "x", "main"), In("acme/a", 3, "y", "main") };
        Assert.Null(PullRequestStacks.MergeOrderWarning(prs, prs[0]));
        Assert.Empty(PullRequestStacks.Above(prs, prs[0]).Children);
    }

    [Fact]
    public void ASameRepoPrWhoseHeadIsTheDefaultBranch_NeverLinks()
    {
        var prs = new[] { In("acme/a", 1, "main", "dev"), In("acme/a", 2, "x", "main") };
        Assert.False(PullRequestStacks.Stacked(prs[0], prs[1]));
    }

    [Fact]
    public void ForkHeadsNeverLink_EvenWithMatchingBranchNames()
    {
        var prs = new[] { In("acme/a", 1, "feat", "main"), In("acme/a", 2, "other", "feat", fork: true) };
        Assert.False(PullRequestStacks.Stacked(prs[0], prs[1]));
    }

    [Fact]
    public void Chain_DoesNotLinkAcrossReposOrThroughForks()
    {
        var other = new[] { In("acme/a", 1, "feat", "main"), In("acme/b", 2, "other", "feat") };
        Assert.Null(PullRequestStacks.Chain(other, other[0].Url));
        var fork = new[] { In("acme/a", 1, "feat", "main"), In("acme/a", 2, "other", "feat", fork: true) };
        Assert.Null(PullRequestStacks.Chain(fork, fork[0].Url));
        var healthy = new[] { In("acme/a", 1, "feat", "main"), In("acme/a", 2, "other", "feat") };
        Assert.Equal([1, 2], PullRequestStacks.Chain(healthy, healthy[0].Url)!.Value.RootToTop.Select(p => p.Number));
    }
}

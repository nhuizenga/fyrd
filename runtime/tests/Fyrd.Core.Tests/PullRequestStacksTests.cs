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
}

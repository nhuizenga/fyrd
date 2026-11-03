namespace Fyrd.Core.Hub;

/// <summary>What <see cref="PullRequestStacks.Owned"/> found: every URL the session owns, and the subset owned only through a stack.</summary>
public sealed record OwnedSet(IReadOnlyList<string> Urls, IReadOnlyList<string> Stacked)
{
    public static readonly OwnedSet Empty = new([], []);
}

/// <summary>
/// The ownership rule for pull requests (and the stack graph it follows). A session owns:
/// <list type="bullet">
/// <item><b>Direct:</b> every PR in the repo listing, of any state, whose head is one of its branches.</item>
/// <item><b>Stacked:</b> from each <i>open</i> direct PR, the open PRs reachable along stack links, all the way along the
/// chain. Up: the open PR whose head is this PR's base. Down: every open PR whose base is this PR's head.</item>
/// </list>
/// Never links through the default branch (PRs that merely target <c>main</c> are not stacked), ignores merged and closed PRs
/// while walking (so a reused branch name cannot pull in old history) and ignores heads in forks. Pure, so it is easy to mirror
/// in the console (<c>stacks.ts</c>).
/// </summary>
public static class PullRequestStacks
{
    public static OwnedSet Owned(PullRequestListing listing, IEnumerable<string?> branches)
    {
        var wanted = branches.Where(b => !string.IsNullOrEmpty(b)).ToHashSet(StringComparer.Ordinal);
        var direct = listing.Prs.Where(p => wanted.Contains(p.Branch)).ToList();
        var directUrls = direct.Select(p => p.Url).ToHashSet(StringComparer.Ordinal);

        var openPrs = listing.Prs.Where(p => p.State == "open" && !p.CrossRepository).ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<PullRequestHead>(direct.Where(p => p.State == "open" && !p.CrossRepository));
        foreach (var p in queue) seen.Add(p.Url);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var next in Neighbours(openPrs, current, listing.DefaultBranch))
                if (seen.Add(next.Url)) queue.Enqueue(next);
        }

        var stacked = seen.Where(u => !directUrls.Contains(u)).ToList();
        return new OwnedSet(
            directUrls.Concat(stacked).Distinct(StringComparer.Ordinal).OrderBy(Number).ToList(),
            stacked.OrderBy(Number).ToList());
    }

    /// <summary>The open PR below this one (its base is that PR's head) and every open PR above it, never through the default branch.</summary>
    private static IEnumerable<PullRequestHead> Neighbours(IReadOnlyList<PullRequestHead> open, PullRequestHead pr, string? defaultBranch)
    {
        if (pr.Base != "" && pr.Base != defaultBranch)
            foreach (var up in open.Where(o => o.Branch == pr.Base)) yield return up;
        if (pr.Branch != defaultBranch)
            foreach (var down in open.Where(o => o.Base == pr.Branch)) yield return down;
    }

    public static int Number(string url) => int.TryParse(url[(url.LastIndexOf('/') + 1)..], out var n) ? n : 0;
}

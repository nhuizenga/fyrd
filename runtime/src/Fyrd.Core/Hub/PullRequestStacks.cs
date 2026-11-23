namespace Fyrd.Core.Hub;

/// <summary>What <see cref="PullRequestStacks.Owned"/> found: every URL the session owns, and the subset owned only through a stack.</summary>
public sealed record OwnedSet(IReadOnlyList<string> Urls, IReadOnlyList<string> Stacked)
{
    public static readonly OwnedSet Empty = new([], []);

    /// <summary>The listing's entries for the direct matches (head is one of the session's branches), with state and creation time.</summary>
    public IReadOnlyList<PullRequestHead> Direct { get; init; } = [];

    /// <summary>The repo's default branch as the listing gave it, or null when it did not say.</summary>
    public string? DefaultBranch { get; init; }

    /// <summary>
    /// The stacked PRs reachable from a direct match whose head is a branch of the session's own, not the default branch (a
    /// session on <c>main</c> matches every PR from <c>main</c>); empty when the listing did not say which branch is the
    /// default. A subset of <see cref="Stacked"/>, which still walks from every open direct match for the live view.
    /// </summary>
    public IReadOnlyList<string> OwnStacked { get; init; } = [];
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

        var starts = direct.Where(p => p.State == "open" && !p.CrossRepository).ToList();
        var def = listing.DefaultBranch;
        var stacked = Walk(listing, starts).Where(u => !directUrls.Contains(u)).ToList();
        var ownStacked = string.IsNullOrEmpty(def) ? []
            : Walk(listing, starts.Where(p => p.Branch != def)).Where(u => !directUrls.Contains(u)).ToList();
        return new OwnedSet(
            directUrls.Concat(stacked).Distinct(StringComparer.Ordinal).OrderBy(Number).ToList(),
            stacked.OrderBy(Number).ToList())
        {
            Direct = direct,
            DefaultBranch = def,
            OwnStacked = ownStacked.OrderBy(Number).ToList(),
        };
    }

    /// <summary>Every open same-repo PR reachable along stack links from <paramref name="starts"/>, the starts included.</summary>
    private static HashSet<string> Walk(PullRequestListing listing, IEnumerable<PullRequestHead> starts)
    {
        var openPrs = listing.Prs.Where(p => p.State == "open" && !p.CrossRepository).ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<PullRequestHead>(starts);
        foreach (var p in queue) seen.Add(p.Url);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var next in Neighbours(openPrs, current, listing.DefaultBranch))
                if (seen.Add(next.Url)) queue.Enqueue(next);
        }
        return seen;
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

    /// <summary>
    /// <paramref name="child"/> is stacked on <paramref name="parent"/>: its base is the parent's head, in the same repo, with
    /// neither head in a fork and the parent's head not the default branch (PRs that merely target <c>main</c> are not stacked).
    /// </summary>
    public static bool Stacked(PullRequestStatus parent, PullRequestStatus child) =>
        parent.Url != child.Url && child.Base != "" && child.Base == parent.Head
        && !parent.CrossRepository && !child.CrossRepository
        && (parent.DefaultBranch == "" || parent.Head != parent.DefaultBranch)
        && SameRepo(parent.Url, child.Url);

    private static bool SameRepo(string a, string b) =>
        string.Equals(a[..a.LastIndexOf("/pull/", StringComparison.Ordinal)], b[..b.LastIndexOf("/pull/", StringComparison.Ordinal)],
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The open PRs stacked above <paramref name="current"/> among <paramref name="open"/> (its children, their children, ...).
    /// <c>Children</c> are the direct ones; <c>TopDown</c> lists every descendant from the top of the stack down, so merging in
    /// that order never leaves a PR targeting a merged branch.
    /// </summary>
    public static (IReadOnlyList<PullRequestStatus> Children, IReadOnlyList<PullRequestStatus> TopDown) Above(
        IEnumerable<PullRequestStatus> open, PullRequestStatus current)
    {
        var candidates = open.Where(p => p.State == "open" && p.Url != current.Url).ToList();
        var children = candidates.Where(p => Stacked(current, p)).OrderBy(p => p.Number).ToList();
        var levels = new List<List<PullRequestStatus>>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { current.Url };
        var frontier = children;
        while (frontier.Count > 0)
        {
            var level = frontier.Where(p => seen.Add(p.Url)).ToList();
            if (level.Count == 0) break;
            levels.Add(level);
            frontier = candidates.Where(c => level.Any(l => Stacked(l, c))).ToList();
        }
        levels.Reverse();
        return (children, levels.SelectMany(l => l.OrderBy(p => p.Number)).ToList());
    }

    /// <summary>The merge-order warning for a PR that has open PRs stacked on it, or null when it has none.</summary>
    public static string? MergeOrderWarning(IEnumerable<PullRequestStatus> open, PullRequestStatus current)
    {
        var (children, topDown) = Above(open, current);
        if (children.Count == 0) return null;
        static string N(PullRequestStatus p) => $"#{p.Number}";
        var kids = children.Count == 1 ? $"{N(children[0])} is" : $"{string.Join(", ", children.Select(N))} are";
        var order = string.Join(", then ", topDown.Select(N)) + ", then this";
        return $"{kids} stacked on this PR. Merge from the top of the stack down: merge {order}. " +
               $"Merging this first leaves {string.Join(", ", children.Select(N))} targeting a merged branch.";
    }

    /// <summary>
    /// The chain of open PRs the PR at <paramref name="url"/> belongs to, root first, or null when it is not stacked with
    /// another. Walks up to the root, then down through the children; where a PR has several open children the chain follows
    /// the lowest number and <c>Fork</c> is set (a fork anywhere in the chain counts).
    /// </summary>
    public static (IReadOnlyList<PullRequestStatus> RootToTop, bool Fork)? Chain(IEnumerable<PullRequestStatus> statuses, string url)
    {
        var open = statuses.Where(p => p.State == "open").ToList();
        var start = open.FirstOrDefault(p => p.Url == url);
        if (start is null) return null;
        PullRequestStatus? Parent(PullRequestStatus p) => open.FirstOrDefault(o => Stacked(o, p));
        List<PullRequestStatus> Kids(PullRequestStatus p) => open.Where(o => Stacked(p, o)).OrderBy(o => o.Number).ToList();

        var root = start;
        var seen = new HashSet<string>(StringComparer.Ordinal) { start.Url };
        for (var p = Parent(root); p is not null && !seen.Contains(p.Url); p = Parent(root)) { seen.Add(p.Url); root = p; }

        var chain = new List<PullRequestStatus> { root };
        var visited = new HashSet<string>(StringComparer.Ordinal) { root.Url };
        var fork = false;
        for (var cur = root; ;)
        {
            var kids = Kids(cur).Where(k => !visited.Contains(k.Url)).ToList();
            if (kids.Count == 0) break;
            if (kids.Count > 1) fork = true;
            cur = kids[0];
            visited.Add(cur.Url);
            chain.Add(cur);
        }
        if (!fork) fork = chain.Any(c => Kids(c).Count > 1);
        return chain.Count < 2 ? null : (chain, fork);
    }
}

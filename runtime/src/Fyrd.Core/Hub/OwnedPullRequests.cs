namespace Fyrd.Core.Hub;

/// <summary>
/// The pull requests a session owns (see <see cref="PullRequestStacks"/>): those whose head is one of its branches, plus every
/// open PR stacked with them. Not 1:1: every session on a PR's branch gets it. The cloud sessions API carries no PR links, so
/// each repo's recent PRs are listed from GitHub (one GraphQL request, with statuses), at most once per <see cref="Ttl"/>. A
/// failed listing keeps the last good one and is not retried until the TTL passes; a rate-limited one is not retried before
/// GitHub's reset time. With no GitHub token there is no listing, so no session owns any PR. An owned open PR the listing
/// read only partially (it is not among the most recently updated) is then read on its own, once per listing.
/// Not thread safe: used by the sequential hub poll.
/// </summary>
public sealed class OwnedPullRequests(IGitHub github, TimeProvider time)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    /// <summary>At most this many owned PRs the listing left partial are read one by one per call; the rest wait a poll.</summary>
    public const int MaxDetailReads = 10;

    private sealed record Listing(DateTimeOffset At, PullRequestListing Value);

    private readonly Dictionary<string, Listing> _byRepo = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _notBefore = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public async Task<IReadOnlyList<string>> FindAsync(string? repo, IEnumerable<string?> branches, CancellationToken ct = default) =>
        (await FindOwnedAsync(repo, branches, ct)).Urls;

    /// <summary>The session's PRs and, among them, those owned only through a stack.</summary>
    public async Task<OwnedSet> FindOwnedAsync(string? repo, IEnumerable<string?> branches, CancellationToken ct = default)
    {
        var wanted = branches.Where(b => !string.IsNullOrEmpty(b)).ToList();
        if (wanted.Count == 0 || !GitHubRepo.IsValid(repo) || !github.Configured) return OwnedSet.Empty;
        var now = time.GetUtcNow();
        Listing? listing;
        lock (_gate) _byRepo.TryGetValue(repo!, out listing);
        DateTimeOffset until;
        bool limited;
        lock (_gate) limited = _notBefore.TryGetValue(repo!, out until) && now < until;
        if ((listing is null || now - listing.At > Ttl) && !limited)
        {
            try
            {
                listing = new Listing(now, await github.ListAsync(repo!, ct));
            }
            catch (GitHubRateLimitException e)
            {
                lock (_gate) _notBefore[repo!] = e.ResetAt;
                listing = new Listing(now, listing?.Value ?? new PullRequestListing(null, []));
            }
            catch (GitHubException)
            {
                listing = new Listing(now, listing?.Value ?? new PullRequestListing(null, []));
            }
            lock (_gate) _byRepo[repo!] = listing;
        }
        if (listing is null) return OwnedSet.Empty;
        var set = PullRequestStacks.Owned(listing.Value, wanted);
        await DetailAsync(repo!, set.Urls, ct);
        return set;
    }

    /// <summary>
    /// Reads each owned PR the listing left partial on its own and stores the full status in the listing, so each is read at
    /// most once per listing (a failed read keeps the partial status until then). A rate limit stops the reads and the listing.
    /// </summary>
    private async Task DetailAsync(string repo, IReadOnlyList<string> urls, CancellationToken ct)
    {
        List<string> partial;
        lock (_gate)
        {
            if (!_byRepo.TryGetValue(repo, out var l)) return;
            var owned = new HashSet<string>(urls, StringComparer.Ordinal);
            partial = l.Value.Prs.Where(p => p.Partial && owned.Contains(p.Url)).Select(p => p.Url).Take(MaxDetailReads).ToList();
        }
        foreach (var url in partial)
        {
            PullRequestStatus? status;
            try { status = await github.GetAsync(url, ct); }
            catch (GitHubRateLimitException e) { lock (_gate) _notBefore[repo] = e.ResetAt; return; }
            // A failed read keeps the partial status until the next listing, so a broken PR is not re-read every poll.
            catch (GitHubException) { status = null; }
            lock (_gate)
            {
                if (!_byRepo.TryGetValue(repo, out var l)) return;
                var prs = l.Value.Prs.Select(p => p.Url == url && p.Partial ? p with { Status = status ?? p.Status, Partial = false } : p).ToList();
                _byRepo[repo] = l with { Value = l.Value with { Prs = prs } };
            }
        }
    }

    /// <summary>The last listed status of a PR, or null when no listing has it.</summary>
    public PullRequestStatus? Status(string url)
    {
        lock (_gate)
            return _byRepo.Values.SelectMany(l => l.Value.Prs).FirstOrDefault(p => p.Url == url)?.Status;
    }

    /// <summary>Makes the repo's next poll list again (after a console action). The old listing stays as the fallback if that fails.</summary>
    public void Invalidate(string repo)
    {
        lock (_gate)
            if (_byRepo.TryGetValue(repo, out var l)) _byRepo[repo] = l with { At = DateTimeOffset.MinValue };
    }
}

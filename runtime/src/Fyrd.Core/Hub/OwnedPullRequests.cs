namespace Fyrd.Core.Hub;

/// <summary>
/// The pull requests a session owns: those whose head is any of the session's branches. Not 1:1: every session on a
/// PR's branch gets it. The cloud sessions API carries no PR
/// links, so each repo's recent PRs are listed from GitHub, at most once per <see cref="Ttl"/>. A failed listing keeps
/// the last good one and is not retried until the TTL passes; a rate-limited one is not retried before GitHub's reset time.
/// With no GitHub token there is no listing at all, so no session owns any PR.
/// Not thread safe: used by the sequential hub poll.
/// </summary>
public sealed class OwnedPullRequests(IGitHub github, TimeProvider time)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);

    private sealed record Listing(DateTimeOffset At, IReadOnlyList<PullRequestHead> Prs);

    private readonly Dictionary<string, Listing> _byRepo = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _notBefore = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<string>> FindAsync(string? repo, IEnumerable<string?> branches, CancellationToken ct = default)
    {
        var wanted = branches.Where(b => !string.IsNullOrEmpty(b)).ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0 || !GitHubRepo.IsValid(repo) || !github.Configured) return [];
        var now = time.GetUtcNow();
        if ((!_byRepo.TryGetValue(repo!, out var listing) || now - listing.At > Ttl)
            && (!_notBefore.TryGetValue(repo!, out var until) || now >= until))
        {
            try
            {
                listing = new Listing(now, await github.ListAsync(repo!, ct));
            }
            catch (GitHubRateLimitException e)
            {
                _notBefore[repo!] = e.ResetAt;
                listing = new Listing(now, listing?.Prs ?? []);
            }
            catch (GitHubException)
            {
                listing = new Listing(now, listing?.Prs ?? []);
            }
            _byRepo[repo!] = listing;
        }
        return (_byRepo.TryGetValue(repo!, out listing) ? listing.Prs : [])
            .Where(p => wanted.Contains(p.Branch))
            .Select(p => p.Url)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(Number)
            .ToList();
    }

    private static int Number(string url) => int.TryParse(url[(url.LastIndexOf('/') + 1)..], out var n) ? n : 0;
}

namespace Fyrd.Core.Hub;

/// <summary>
/// The pull requests a session owns: those whose head is the session's live branch. The cloud sessions API carries no PR
/// links, so each repo's recent PRs are listed through <c>gh</c>, at most once per <see cref="Ttl"/>. A failed listing keeps
/// the last good one and is not retried until the TTL passes, so a missing or signed-out <c>gh</c> costs one call per TTL.
/// Not thread safe: used by the sequential hub poll.
/// </summary>
public sealed class OwnedPullRequests(IGitHub github, TimeProvider time)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);

    private sealed record Listing(DateTimeOffset At, IReadOnlyList<PullRequestHead> Prs);

    private readonly Dictionary<string, Listing> _byRepo = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<string>> FindAsync(string? repo, string? branch, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(branch) || !GitHubRepo.IsValid(repo)) return [];
        var now = time.GetUtcNow();
        if (!_byRepo.TryGetValue(repo!, out var listing) || now - listing.At > Ttl)
        {
            try
            {
                listing = new Listing(now, await github.ListAsync(repo!, ct));
            }
            catch (GitHubException)
            {
                listing = new Listing(now, listing?.Prs ?? []);
            }
            _byRepo[repo!] = listing;
        }
        return listing.Prs
            .Where(p => p.Branch == branch)
            .Select(p => p.Url)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(Number)
            .ToList();
    }

    private static int Number(string url) => int.TryParse(url[(url.LastIndexOf('/') + 1)..], out var n) ? n : 0;
}

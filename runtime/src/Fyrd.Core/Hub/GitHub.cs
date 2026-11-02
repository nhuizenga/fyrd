using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fyrd.Core.Hub;

/// <summary>
/// A pull request as the console shows it. <c>State</c> is <c>open</c>, <c>merged</c>, <c>closed</c> or <c>unknown</c> (GitHub
/// could not be read; see <c>Error</c>). <c>Checks</c> is <c>none</c>, <c>pending</c>, <c>passing</c> or <c>failing</c>.
/// </summary>
public sealed record PullRequestStatus(string Url, int Number, string Title, string State, bool Draft, string Checks, string? Error = null)
{
    /// <summary>The branch the PR merges into, and the one it merges from.</summary>
    public string Base { get; init; } = "";
    public string Head { get; init; } = "";

    public int ChecksPassed { get; init; }
    public int ChecksFailed { get; init; }
    public int ChecksPending { get; init; }

    /// <summary>The first failing check, or null.</summary>
    public FailingCheck? FailingCheck { get; init; }

    /// <summary><c>none</c>, <c>commented</c>, <c>approved</c> or <c>changes_requested</c>.</summary>
    public string Review { get; init; } = "none";

    /// <summary><c>mergeable</c>, <c>conflicting</c> or <c>unknown</c>.</summary>
    public string Mergeable { get; init; } = "unknown";

    /// <summary>Lower-cased <c>mergeStateStatus</c>: clean, behind, blocked, dirty, unstable, draft, has_hooks or unknown.</summary>
    public string MergeState { get; init; } = "unknown";

    /// <summary>
    /// Open, out of draft, no check failing or still running, and no conflicts. Not gated on <c>blocked</c>: GitHub's own
    /// branch rules answer that, and the merge error is shown.
    /// </summary>
    public bool CanMerge => State == "open" && !Draft && Checks is "passing" or "none" && Mergeable != "conflicting";
}

/// <summary>A failing check's name and where to read why (null when GitHub gave no link).</summary>
public sealed record FailingCheck(string Name, string? Url);

public class GitHubException(string message) : Exception(message);

/// <summary>No GitHub token is set, so nothing was sent to GitHub.</summary>
public sealed class GitHubNotConfiguredException() : GitHubException("GitHub is not configured. Set GH_TOKEN before /fyrd:up.");

/// <summary>GitHub's rate limit is spent until <see cref="ResetAt"/>.</summary>
public sealed class GitHubRateLimitException(DateTimeOffset resetAt)
    : GitHubException($"GitHub's rate limit is used up until {resetAt:HH:mm} UTC.")
{
    public DateTimeOffset ResetAt { get; } = resetAt;
}

/// <summary>
/// The GitHub token, read once at startup from <c>GH_TOKEN</c>, else <c>GITHUB_TOKEN</c>. It is kept in memory only:
/// <see cref="ToString"/> never shows it, so it cannot reach a log or an error message by accident.
/// </summary>
public sealed class GitHubToken(string? value)
{
    private readonly string? _value = string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public bool Present => _value is not null;
    public string Value => _value ?? throw new GitHubNotConfiguredException();

    public static GitHubToken FromEnvironment() =>
        new(Environment.GetEnvironmentVariable("GH_TOKEN") is { Length: > 0 } t ? t : Environment.GetEnvironmentVariable("GITHUB_TOKEN"));

    public override string ToString() => Present ? "GitHubToken(set)" : "GitHubToken(not set)";
}

/// <summary>
/// A pull request from a repo listing: the branch it merges from, plus what stacking needs (its base, state and whether the
/// head is in a fork) and its status. Open PRs carry the full rollup; the rest only title, state and draft.
/// </summary>
public sealed record PullRequestHead(string Url, string Branch)
{
    public string Base { get; init; } = "";
    /// <summary><c>open</c>, <c>merged</c> or <c>closed</c>; <c>unknown</c> when the listing did not say.</summary>
    public string State { get; init; } = "unknown";
    public bool CrossRepository { get; init; }
    public PullRequestStatus? Status { get; init; }
}

/// <summary>A repo's recent pull requests (any state) and its default branch, which stacks never link through.</summary>
public sealed record PullRequestListing(string? DefaultBranch, IReadOnlyList<PullRequestHead> Prs);

/// <summary>What Fyrd does to a session's pull requests. Never sees a URL that failed <see cref="PullRequestUrl.IsValid"/>.</summary>
public interface IGitHub
{
    /// <summary>False when no token is set: every other call then throws <see cref="GitHubNotConfiguredException"/> without a network call.</summary>
    bool Configured { get; }

    /// <summary>The login the token belongs to, or null when no token is set.</summary>
    Task<string?> LoginAsync(CancellationToken ct = default);

    /// <summary>The repo's most recent pull requests (any state). Never sees a repo that failed <see cref="GitHubRepo.IsValid"/>.</summary>
    Task<PullRequestListing> ListAsync(string repo, CancellationToken ct = default);
    Task<PullRequestStatus> GetAsync(string url, CancellationToken ct = default);
    Task MarkReadyAsync(string url, CancellationToken ct = default);
    Task MergeAsync(string url, CancellationToken ct = default);
}

public static partial class PullRequestUrl
{
    [GeneratedRegex(@"^https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/pull/[0-9]+$")]
    private static partial Regex Pattern();

    /// <summary>Plain <c>https://github.com/{owner}/{repo}/pull/{n}</c> only, so nothing else ever reaches a query.</summary>
    public static bool IsValid(string? url) => url is not null && Pattern().IsMatch(url);

    /// <summary>Owner, repo and number of a valid URL; throws <see cref="GitHubException"/> for anything else.</summary>
    public static (string Owner, string Repo, int Number) Parse(string url)
    {
        if (!IsValid(url)) throw new GitHubException("Not a GitHub pull request URL.");
        var parts = url["https://github.com/".Length..].Split('/');
        return (parts[0], parts[1], int.Parse(parts[3]));
    }
}

public static partial class GitHubRepo
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9_.-]+$")]
    private static partial Regex Pattern();

    /// <summary>Plain <c>{owner}/{repo}</c> only, so nothing else ever reaches a query.</summary>
    public static bool IsValid(string? repo) => repo is not null && Pattern().IsMatch(repo);
}

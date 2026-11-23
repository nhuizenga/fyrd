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

    /// <summary>Every failing check (the first is <see cref="FailingCheck"/>).</summary>
    public IReadOnlyList<FailingCheck> FailingChecks { get; init; } = [];

    /// <summary><c>none</c>, <c>commented</c>, <c>approved</c> or <c>changes_requested</c>.</summary>
    public string Review { get; init; } = "none";

    /// <summary><c>mergeable</c>, <c>conflicting</c> or <c>unknown</c>.</summary>
    public string Mergeable { get; init; } = "unknown";

    /// <summary>The commit the PR head points at (empty when unknown).</summary>
    public string HeadSha { get; init; } = "";

    /// <summary>The head is in a fork. Such a PR never links into a stack.</summary>
    public bool CrossRepository { get; init; }

    /// <summary>The repo's default branch (empty when unknown). A PR whose head is the default branch never links into a stack.</summary>
    public string DefaultBranch { get; init; } = "";

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

/// <summary>GitHub can read the repo but has no pull request with that number.</summary>
public sealed class GitHubPullRequestNotFoundException(string url) : GitHubException($"GitHub has no pull request at {url}.");

/// <summary>No GitHub token is set, so nothing was sent to GitHub.</summary>
public sealed class GitHubNotConfiguredException() : GitHubException("GitHub is not configured. Set GH_TOKEN before /fyrd:up.");

/// <summary>GitHub's rate limit is spent until <see cref="ResetAt"/>.</summary>
public sealed class GitHubRateLimitException(DateTimeOffset resetAt)
    : GitHubException($"GitHub's rate limit is used up until {resetAt:HH:mm} UTC.")
{
    public DateTimeOffset ResetAt { get; } = resetAt;
}

/// <summary>
/// The GitHub token: a saved token (set from Settings) wins over the one read at startup from <c>GH_TOKEN</c>, else
/// <c>GITHUB_TOKEN</c>. It is kept in memory only: <see cref="ToString"/> never shows it, so it cannot reach a log or an
/// error message by accident. Removing the saved token reverts to the environment token.
/// </summary>
public sealed class GitHubToken(string? value)
{
    private readonly string? _env = Clean(value);
    private volatile string? _saved;

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    /// <summary>Raised after <see cref="SetSaved"/> changes the effective token.</summary>
    public event Action? Changed;

    public bool Present => (_saved ?? _env) is not null;
    public string Value => _saved ?? _env ?? throw new GitHubNotConfiguredException();
    public bool FromEnvironmentOnly => _saved is null && _env is not null;

    /// <summary>Sets (or, with null/blank, clears) the saved token that overrides the environment one.</summary>
    public void SetSaved(string? token)
    {
        _saved = Clean(token);
        Changed?.Invoke();
    }

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

    /// <summary>When the PR was opened, or null when the listing did not say.</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>An open PR the listing did not read in full (no checks, review or mergeability): read it on its own when needed.</summary>
    public bool Partial { get; init; }
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
    /// <summary>
    /// Squash-merges the PR. With <paramref name="headSha"/> GitHub merges only if the head is still that commit, so a push
    /// after the status was read is never squashed unchecked (the merge then fails with a message saying the PR changed).
    /// </summary>
    Task MergeAsync(string url, string? headSha = null, CancellationToken ct = default);
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
        // The pattern allows any digits: a number past int.MaxValue is no PR, and must not escape as an OverflowException.
        if (!int.TryParse(parts[3], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number))
            throw new GitHubException("Not a GitHub pull request URL.");
        return (parts[0], parts[1], number);
    }
}

public static partial class GitHubRepo
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9_.-]+$")]
    private static partial Regex Pattern();

    /// <summary>Plain <c>{owner}/{repo}</c> only, so nothing else ever reaches a query.</summary>
    public static bool IsValid(string? repo) => repo is not null && Pattern().IsMatch(repo);
}

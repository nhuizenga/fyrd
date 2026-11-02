using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fyrd.Core.Hub;

/// <summary>
/// A pull request as the console shows it. <c>State</c> is <c>open</c>, <c>merged</c>, <c>closed</c> or <c>unknown</c> (GitHub
/// could not be read; see <c>Error</c>). <c>Checks</c> is <c>none</c>, <c>pending</c>, <c>passing</c> or <c>failing</c>.
/// </summary>
public sealed record PullRequestStatus(string Url, int Number, string Title, string State, bool Draft, string Checks, string? Error = null)
{
    /// <summary>Open, out of draft, and no check failing or still running.</summary>
    public bool CanMerge => State == "open" && !Draft && Checks is "passing" or "none";
}

public sealed class GitHubException(string message) : Exception(message);

/// <summary>A pull request and the branch it merges from.</summary>
public sealed record PullRequestHead(string Url, string Branch);

/// <summary>What Fyrd does to a session's pull requests. Never sees a URL that failed <see cref="PullRequestUrl.IsValid"/>.</summary>
public interface IGitHub
{
    /// <summary>The repo's most recent pull requests (any state). Never sees a repo that failed <see cref="GitHubRepo.IsValid"/>.</summary>
    Task<IReadOnlyList<PullRequestHead>> ListAsync(string repo, CancellationToken ct = default);
    Task<PullRequestStatus> GetAsync(string url, CancellationToken ct = default);
    Task MarkReadyAsync(string url, CancellationToken ct = default);
    Task MergeAsync(string url, CancellationToken ct = default);
}

public static partial class PullRequestUrl
{
    [GeneratedRegex(@"^https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/pull/[0-9]+$")]
    private static partial Regex Pattern();

    /// <summary>Plain <c>https://github.com/{owner}/{repo}/pull/{n}</c> only, so a URL can never act as a CLI option.</summary>
    public static bool IsValid(string? url) => url is not null && Pattern().IsMatch(url);

    [GeneratedRegex(@"https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/pull/[0-9]+")]
    private static partial Regex Embedded();

    /// <summary>Every plain pull request URL inside free text, in order.</summary>
    public static IEnumerable<string> FindIn(string text) =>
        Embedded().Matches(text).Select(m => m.Value).Where(IsValid);
}

public static partial class GitHubRepo
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9_.-]+$")]
    private static partial Regex Pattern();

    /// <summary>Plain <c>{owner}/{repo}</c> only, so a repo can never act as a CLI option.</summary>
    public static bool IsValid(string? repo) => repo is not null && Pattern().IsMatch(repo);
}

/// <summary>Drives the owner's signed-in <c>gh</c> CLI. Arguments are passed as a list, never through a shell.</summary>
public sealed class GhCli : IGitHub
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public async Task<IReadOnlyList<PullRequestHead>> ListAsync(string repo, CancellationToken ct = default) =>
        ParseList(await RunAsync(
            ["pr", "list", "--repo=" + (GitHubRepo.IsValid(repo) ? repo : throw new GitHubException("Not a GitHub repo.")),
             "--state=all", "--limit=100", "--json=url,headRefName"], ct));

    /// <summary>Maps <c>gh pr list --json url,headRefName</c> output, skipping entries without a plain PR URL or a branch.</summary>
    public static IReadOnlyList<PullRequestHead> ParseList(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
        return doc.RootElement.EnumerateArray()
            .Select(e => (Url: Str(e, "url"), Branch: Str(e, "headRefName")))
            .Where(p => PullRequestUrl.IsValid(p.Url) && !string.IsNullOrEmpty(p.Branch))
            .Select(p => new PullRequestHead(p.Url!, p.Branch!))
            .ToList();
    }

    public async Task<PullRequestStatus> GetAsync(string url, CancellationToken ct = default) =>
        Parse(url, await RunAsync(["pr", "view", Check(url), "--json", "number,title,state,isDraft,statusCheckRollup"], ct));

    public async Task MarkReadyAsync(string url, CancellationToken ct = default) =>
        await RunAsync(["pr", "ready", Check(url)], ct);

    // A merge commit, matching how this repo's history is merged.
    public async Task MergeAsync(string url, CancellationToken ct = default) =>
        await RunAsync(["pr", "merge", Check(url), "--merge"], ct);

    private static string Check(string url) =>
        PullRequestUrl.IsValid(url) ? url : throw new GitHubException("Not a GitHub pull request URL.");

    /// <summary>Maps <c>gh pr view --json number,title,state,isDraft,statusCheckRollup</c> output.</summary>
    public static PullRequestStatus Parse(string url, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var state = Str(r, "state")?.ToLowerInvariant() switch { "open" => "open", "merged" => "merged", _ => "closed" };
        var draft = r.TryGetProperty("isDraft", out var d) && d.ValueKind == JsonValueKind.True;
        var number = r.TryGetProperty("number", out var n) && n.TryGetInt32(out var num) ? num : 0;

        bool failing = false, pending = false;
        var any = false;
        if (r.TryGetProperty("statusCheckRollup", out var rollup) && rollup.ValueKind == JsonValueKind.Array)
            foreach (var c in rollup.EnumerateArray())
            {
                any = true;
                // A CheckRun has status + conclusion; a commit StatusContext has just state.
                var status = Str(c, "status")?.ToUpperInvariant();
                var conclusion = Str(c, "conclusion")?.ToUpperInvariant();
                var st = Str(c, "state")?.ToUpperInvariant();
                if (status is not null && status != "COMPLETED") pending = true;
                else if (conclusion is "FAILURE" or "TIMED_OUT" or "CANCELLED" or "ACTION_REQUIRED" or "STARTUP_FAILURE" or "STALE") failing = true;
                else if (st is "FAILURE" or "ERROR") failing = true;
                else if (st is "PENDING" or "EXPECTED") pending = true;
            }
        var checks = failing ? "failing" : pending ? "pending" : any ? "passing" : "none";
        return new PullRequestStatus(url, number, Str(r, "title") ?? "", state, draft, checks);
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static async Task<string> RunAsync(IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("gh") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GH_PROMPT_DISABLED"] = "1";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        Process p;
        try { p = Process.Start(psi) ?? throw new GitHubException("Could not start gh."); }
        catch (System.ComponentModel.Win32Exception) { throw new GitHubException("The GitHub CLI (gh) is not installed or not on PATH."); }
        using (p)
        {
            p.StandardInput.Close();
            var stdout = p.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = p.StandardError.ReadToEndAsync(cts.Token);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { p.Kill(true); } catch (InvalidOperationException) { }
                throw new GitHubException("gh timed out.");
            }
            if (p.ExitCode != 0)
            {
                var err = (await stderr).Trim();
                throw new GitHubException(err.Length > 0 ? err : $"gh exited with {p.ExitCode}.");
            }
            return await stdout;
        }
    }
}

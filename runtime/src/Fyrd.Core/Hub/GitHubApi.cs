using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Octokit;

namespace Fyrd.Core.Hub;

/// <summary>
/// Talks to GitHub directly: GraphQL (posted with an <see cref="HttpClient"/>) for the status listing and mark-ready, and
/// Octokit for the squash merge and the signed-in user. With no token nothing is sent. The token is only ever put in the
/// <c>Authorization</c> header, never in a message, a log line or state.
/// </summary>
public sealed class GitHubApi(GitHubToken token, HttpClient http, IGitHubClient octokit, TimeProvider? time = null) : IGitHub
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>The fields one pull request is read with (the node's <c>id</c> is what mark-ready needs).</summary>
    internal const string PrFields = """
        id url number title state isDraft headRefName headRefOid baseRefName isCrossRepository baseRepository{defaultBranchRef{name}} reviewDecision mergeable mergeStateStatus
        latestReviews(first:10){nodes{state}}
        commits(last:1){nodes{commit{statusCheckRollup{state contexts(first:100){nodes{__typename
          ... on CheckRun{name status conclusion detailsUrl}
          ... on StatusContext{context state targetUrl}}}}}}}
        """;

    internal const string ListQuery = """
        query($owner:String!,$name:String!){
          rateLimit{cost remaining resetAt}
          repository(owner:$owner,name:$name){
            defaultBranchRef{name}
            all:pullRequests(first:100,orderBy:{field:UPDATED_AT,direction:DESC}){nodes{url number title state isDraft headRefName baseRefName isCrossRepository createdAt}}
            open:pullRequests(first:5,states:OPEN,orderBy:{field:UPDATED_AT,direction:DESC}){nodes{
        """ + PrFields + """
        }}}}
        """;

    internal const string GetQuery = """
        query($owner:String!,$name:String!,$number:Int!){
          repository(owner:$owner,name:$name){pullRequest(number:$number){
        """ + PrFields + """
        }}}
        """;

    internal const string ReadyMutation = """
        mutation($id:ID!){markPullRequestReadyForReview(input:{pullRequestId:$id}){pullRequest{id isDraft}}}
        """;

    public bool Configured => token.Present;

    public async Task<string?> LoginAsync(CancellationToken ct = default)
    {
        if (!token.Present) return null;
        try { return (await octokit.User.Current()).Login; }
        catch (Exception e) when (e is ApiException or HttpRequestException) { throw Map(e, "GitHub"); }
    }

    public async Task<PullRequestListing> ListAsync(string repo, CancellationToken ct = default)
    {
        var parts = (GitHubRepo.IsValid(repo) ? repo : throw new GitHubException("Not a GitHub repo.")).Split('/');
        using var data = await QueryAsync(ListQuery, new { owner = parts[0], name = parts[1] }, repo, ct);
        return ParseList(Data(data));
    }

    /// <summary>
    /// Maps the listing: the light <c>all</c> alias joined with the heavy <c>open</c> alias by URL. Entries without a plain PR
    /// URL or a head branch are skipped. Only the 5 most recently updated open PRs carry the check rollup; the rest are
    /// marked <see cref="PullRequestHead.Partial"/> and an owned one is read on its own (see <see cref="OwnedPullRequests"/>).
    /// Measured on dotnet/runtime against GitHub's ~10s limit: 50 open always 502s, 20 runs 7-9s and sometimes 504s, 10 runs
    /// 4-8s, 5 runs 2-3s. Resolving rollups and mergeability is what is slow; the point cost stays at 1-2.
    /// </summary>
    public static PullRequestListing ParseList(JsonElement data)
    {
        var open = new Dictionary<string, PullRequestStatus>(StringComparer.Ordinal);
        if (Path(data, "repository", "open", "nodes", out var openNodes) && openNodes.ValueKind == JsonValueKind.Array)
            foreach (var e in openNodes.EnumerateArray())
                if (Str(e, "url") is { } u && PullRequestUrl.IsValid(u)) open[PullRequestUrl.Canonical(u)] = ParseStatus(PullRequestUrl.Canonical(u), e);
        var heads = new List<PullRequestHead>();
        if (Path(data, "repository", "all", "nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
            foreach (var e in nodes.EnumerateArray())
            {
                // GitHub spells owner and repo as they are named (mixed case); Fyrd keeps PR URLs in lower case.
                var url = Str(e, "url") is { } raw ? PullRequestUrl.Canonical(raw) : null;
                var branch = Str(e, "headRefName");
                if (!PullRequestUrl.IsValid(url) || string.IsNullOrEmpty(branch)) continue;
                var light = ParseStatus(url!, e);
                heads.Add(new PullRequestHead(url!, branch!)
                {
                    Base = Str(e, "baseRefName") ?? "",
                    State = light.State,
                    CrossRepository = e.TryGetProperty("isCrossRepository", out var x) && x.ValueKind == JsonValueKind.True,
                    Status = open.TryGetValue(url!, out var rich) ? rich : light,
                    Partial = rich is null && light.State == "open",
                    CreatedAt = Str(e, "createdAt") is { } c && DateTimeOffset.TryParse(c, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var created) ? created : null,
                });
            }
        var def = Path(data, "repository", "defaultBranchRef", "name", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
        return new PullRequestListing(def, heads);
    }

    public async Task<PullRequestStatus> GetAsync(string url, CancellationToken ct = default)
    {
        var (owner, repo, number) = PullRequestUrl.Parse(url);
        using var data = await QueryAsync(GetQuery, new { owner, name = repo, number }, $"{owner}/{repo}", ct, url);
        return ParseStatus(PullRequestUrl.Canonical(url), NodeOf(Data(data), url));
    }

    public async Task MarkReadyAsync(string url, CancellationToken ct = default)
    {
        var (owner, repo, number) = PullRequestUrl.Parse(url);
        // The id comes from a fresh read, never from a cache.
        using var data = await QueryAsync(GetQuery, new { owner, name = repo, number }, $"{owner}/{repo}", ct, url);
        var id = Str(NodeOf(Data(data), url), "id") ?? throw new GitHubException("GitHub did not return that pull request's id.");
        using var _ = await QueryAsync(ReadyMutation, new { id }, $"{owner}/{repo}", ct);
    }

    // Always squash: the owner's history is squashed, and no merge-method menu.
    public async Task MergeAsync(string url, string? headSha = null, CancellationToken ct = default)
    {
        var (owner, repo, number) = PullRequestUrl.Parse(url);
        if (!token.Present) throw new GitHubNotConfiguredException();
        try
        {
            var result = await octokit.PullRequest.Merge(owner, repo, number, new MergePullRequest { MergeMethod = PullRequestMergeMethod.Squash, Sha = string.IsNullOrEmpty(headSha) ? null : headSha });
            if (!result.Merged) throw new GitHubException($"GitHub refused the merge: {result.Message}");
        }
        catch (PullRequestMismatchException) { throw new GitHubException("GitHub refused the merge: the pull request changed after it was checked (its head moved). Check it again."); }
        catch (Exception e) when (e is ApiException or HttpRequestException) { throw Map(e, $"{owner}/{repo}", "GitHub refused the merge: "); }
    }

    private static JsonElement Data(JsonDocument doc) => doc.RootElement.GetProperty("data");

    private static JsonElement NodeOf(JsonElement data, string url) =>
        Path(data, "repository", "pullRequest", out var pr) && pr.ValueKind == JsonValueKind.Object
            ? pr : throw new GitHubPullRequestNotFoundException(url);

    /// <summary>Maps one GraphQL pull request node. Check rules: any running check means pending, any failed one failing.</summary>
    public static PullRequestStatus ParseStatus(string url, JsonElement r)
    {
        var state = Str(r, "state")?.ToUpperInvariant() switch { "OPEN" => "open", "MERGED" => "merged", _ => "closed" };
        var draft = r.TryGetProperty("isDraft", out var d) && d.ValueKind == JsonValueKind.True;
        var number = r.TryGetProperty("number", out var n) && n.TryGetInt32(out var num) ? num : 0;

        bool failing = false, pending = false, any = false;
        int passed = 0, failed = 0, running = 0;
        FailingCheck? firstFailing = null;
        var allFailing = new List<FailingCheck>();
        if (Path(r, "commits", "nodes", out var commits) && commits.ValueKind == JsonValueKind.Array && commits.GetArrayLength() > 0
            && Path(commits[0], "commit", "statusCheckRollup", "contexts", "nodes", out var contexts) && contexts.ValueKind == JsonValueKind.Array)
            foreach (var c in contexts.EnumerateArray())
            {
                any = true;
                // A CheckRun has status + conclusion; a commit StatusContext has just state.
                var status = Str(c, "status")?.ToUpperInvariant();
                var conclusion = Str(c, "conclusion")?.ToUpperInvariant();
                var st = Str(c, "state")?.ToUpperInvariant();
                if (status is not null && status != "COMPLETED") { pending = true; running++; }
                else if (conclusion is "FAILURE" or "TIMED_OUT" or "CANCELLED" or "ACTION_REQUIRED" or "STARTUP_FAILURE" or "STALE"
                         || st is "FAILURE" or "ERROR")
                {
                    failing = true;
                    failed++;
                    var fc = new FailingCheck(Str(c, "name") ?? Str(c, "context") ?? "check", Str(c, "detailsUrl") ?? Str(c, "targetUrl"));
                    firstFailing ??= fc;
                    allFailing.Add(fc);
                }
                else if (st is "PENDING" or "EXPECTED") { pending = true; running++; }
                else passed++;
            }
        var checks = failing ? "failing" : pending ? "pending" : any ? "passing" : "none";

        var review = Str(r, "reviewDecision")?.ToUpperInvariant() switch
        {
            "APPROVED" => "approved",
            "CHANGES_REQUESTED" => "changes_requested",
            // REVIEW_REQUIRED and null say nothing about whether anyone has looked: use the latest reviews.
            _ => Path(r, "latestReviews", "nodes", out var reviews) && reviews.ValueKind == JsonValueKind.Array && reviews.GetArrayLength() > 0
                ? "commented" : "none",
        };
        var mergeable = Str(r, "mergeable")?.ToUpperInvariant() switch { "MERGEABLE" => "mergeable", "CONFLICTING" => "conflicting", _ => "unknown" };

        return new PullRequestStatus(url, number, Str(r, "title") ?? "", state, draft, checks)
        {
            Base = Str(r, "baseRefName") ?? "",
            Head = Str(r, "headRefName") ?? "",
            ChecksPassed = passed, ChecksFailed = failed, ChecksPending = running,
            FailingCheck = firstFailing,
            FailingChecks = allFailing,
            Review = review,
            Mergeable = mergeable,
            MergeState = Str(r, "mergeStateStatus")?.ToLowerInvariant() ?? "unknown",
            HeadSha = Str(r, "headRefOid") ?? "",
            CrossRepository = r.TryGetProperty("isCrossRepository", out var xr) && xr.ValueKind == JsonValueKind.True,
            DefaultBranch = Path(r, "baseRepository", "defaultBranchRef", "name", out var db) && db.ValueKind == JsonValueKind.String ? db.GetString()! : "",
        };
    }

    /// <param name="prUrl">The pull request asked for, so a NOT_FOUND on it (not on the repo) says the PR does not exist.</param>
    private async Task<JsonDocument> QueryAsync(string query, object variables, string subject, CancellationToken ct, string? prUrl = null)
    {
        if (!token.Present) throw new GitHubNotConfiguredException();
        using var req = new HttpRequestMessage(HttpMethod.Post, "graphql")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { query, variables }), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Value);
        HttpResponseMessage resp;
        try { resp = await http.SendAsync(req, ct); }
        catch (HttpRequestException e) { throw new GitHubException($"Could not reach GitHub: {e.Message}"); }
        using (resp)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (RateLimited(resp) is { } reset) throw new GitHubRateLimitException(reset);
            if (resp.StatusCode is HttpStatusCode.Unauthorized) throw new GitHubException("GitHub rejected the token (bad or expired).");
            if (!resp.IsSuccessStatusCode) throw new GitHubException($"GitHub answered {(int)resp.StatusCode} for {subject}.");
            JsonDocument doc;
            try { doc = JsonDocument.Parse(body); }
            catch (JsonException) { throw new GitHubException("GitHub sent a response that is not JSON."); }
            var root = doc.RootElement;
            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
            {
                var first = errors[0];
                var message = Str(first, "message") ?? "unknown error";
                var type = Str(first, "type");
                // GitHub reports a missing PR in a readable repo at path [repository, pullRequest]; a missing or hidden repo at [repository].
                var missingPr = first.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.Array && path.GetArrayLength() == 2
                    && path[1].ValueKind == JsonValueKind.String && path[1].GetString() == "pullRequest";
                doc.Dispose();
                if (type == "RATE_LIMITED") throw new GitHubRateLimitException(_time.GetUtcNow().AddMinutes(1));
                if (type == "NOT_FOUND" && prUrl is not null && missingPr)
                    throw new GitHubPullRequestNotFoundException(prUrl);
                if (type == "NOT_FOUND" || type == "FORBIDDEN") throw new GitHubException($"GitHub token lacks access to {subject}.");
                throw new GitHubException($"GitHub refused the request: {message}");
            }
            if (!root.TryGetProperty("data", out _)) { doc.Dispose(); throw new GitHubException("GitHub sent no data."); }
            return doc;
        }
    }

    /// <summary>When the limit lifts, if this response is a 403/429 with <c>x-ratelimit-remaining: 0</c>.</summary>
    private DateTimeOffset? RateLimited(HttpResponseMessage resp)
    {
        if (resp.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)) return null;
        if (!resp.Headers.TryGetValues("x-ratelimit-remaining", out var rem) || rem.FirstOrDefault() != "0") return null;
        return resp.Headers.TryGetValues("x-ratelimit-reset", out var reset) && long.TryParse(reset.FirstOrDefault(), out var epoch)
            ? DateTimeOffset.FromUnixTimeSeconds(epoch)
            : _time.GetUtcNow().AddMinutes(1);
    }

    private static GitHubException Map(Exception e, string subject, string prefix = "") => e switch
    {
        RateLimitExceededException rl => new GitHubRateLimitException(rl.Reset),
        SecondaryRateLimitExceededException => new GitHubRateLimitException(DateTimeOffset.UtcNow.AddMinutes(1)),
        AuthorizationException a when a.StatusCode == HttpStatusCode.Unauthorized => new GitHubException("GitHub rejected the token (bad or expired)."),
        ForbiddenException or NotFoundException => new GitHubException($"GitHub token lacks access to {subject}."),
        ApiException a => new GitHubException(prefix + Clean(a.Message)),
        _ => new GitHubException($"Could not reach GitHub: {e.Message}"),
    };

    private static string Clean(string message) => message.Length > 300 ? message[..300] : message;

    private static bool Path(JsonElement e, string a, string b, out JsonElement found) =>
        Path(e, [a, b], out found);
    private static bool Path(JsonElement e, string a, string b, string c, out JsonElement found) =>
        Path(e, [a, b, c], out found);
    private static bool Path(JsonElement e, string a, string b, string c, string d, out JsonElement found) =>
        Path(e, [a, b, c, d], out found);

    private static bool Path(JsonElement e, string[] names, out JsonElement found)
    {
        found = e;
        foreach (var n in names)
        {
            if (found.ValueKind != JsonValueKind.Object || !found.TryGetProperty(n, out found)) return false;
        }
        return true;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

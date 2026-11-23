using System.Net;
using System.Text;
using System.Text.Json;
using Fyrd.Core.Hub;
using Octokit;
using Octokit.Internal;

namespace Fyrd.Core.Tests;

/// <summary>Answers each request with the next scripted response and records it.</summary>
public sealed class ScriptedHandler : HttpMessageHandler
{
    public sealed record Reply(HttpStatusCode Status, string Body, Dictionary<string, string>? Headers = null);

    private readonly Queue<Reply> _replies = new();
    public List<RecordedRequest> Requests { get; } = [];

    public ScriptedHandler Then(string body, HttpStatusCode status = HttpStatusCode.OK, Dictionary<string, string>? headers = null)
    {
        _replies.Enqueue(new Reply(status, body, headers));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Requests.Add(new RecordedRequest(request, body));
        var r = _replies.Count > 0 ? _replies.Dequeue() : new Reply(HttpStatusCode.OK, "{}");
        var resp = new HttpResponseMessage(r.Status) { Content = new StringContent(r.Body, Encoding.UTF8, "application/json") };
        foreach (var (k, v) in r.Headers ?? []) resp.Headers.TryAddWithoutValidation(k, v);
        return resp;
    }
}

public class GitHubApiTests
{
    private const string Url = "https://github.com/acme/widgets/pull/12";
    private const string Secret = "ghp_supersecrettoken123";

    private static (GitHubApi Api, ScriptedHandler Graph, ScriptedHandler Octo) Make(string? token = Secret)
    {
        var graph = new ScriptedHandler();
        var rest = new ScriptedHandler();
        var http = new HttpClient(graph) { BaseAddress = new Uri("https://api.github.com/") };
        var octokit = new GitHubClient(new Connection(new ProductHeaderValue("fyrd"), new Uri("https://api.github.com/"),
            new InMemoryCredentialStore(new Credentials(token ?? "x")), new HttpClientAdapter(() => rest), new SimpleJsonSerializer()));
        return (new GitHubApi(new GitHubToken(token), http, octokit, new FixedTime(DateTimeOffset.Parse("2026-11-02T12:00:00Z"))), graph, rest);
    }

    private static string Pr(string state = "OPEN", bool draft = false, string rollup = "[]", string mergeable = "MERGEABLE", int number = 12) =>
        """
        {"id":"PR_node12","url":"https://github.com/acme/widgets/pull/@N","number":@N,"title":"Fix it","state":"@STATE","isDraft":@DRAFT,
         "headRefName":"fyrd/a","baseRefName":"main","mergeable":"@MERGEABLE",
         "commits":{"nodes":[{"commit":{"statusCheckRollup":{"state":"X","contexts":{"nodes":@ROLLUP}}}}]}}
        """
        .Replace("@N", number.ToString()).Replace("@STATE", state).Replace("@DRAFT", draft ? "true" : "false")
        .Replace("@MERGEABLE", mergeable).Replace("@ROLLUP", rollup);

    private static string Get(string pr) => """{"data":{"repository":{"pullRequest":@PR}}}""".Replace("@PR", pr);

    [Theory]
    [InlineData(Url, true)]
    [InlineData("https://github.com/acme/widgets/pull/12/", false)]
    [InlineData("http://github.com/acme/widgets/pull/12", false)]
    [InlineData("https://github.com/acme/widgets/issues/12", false)]
    [InlineData("--repo=evil", false)]
    [InlineData("https://github.com/acme/widgets/pull/12 --admin", false)]
    public void Url_OnlyPlainGithubPullUrlsAreAccepted(string url, bool ok) =>
        Assert.Equal(ok, PullRequestUrl.IsValid(url));

    [Fact]
    public void Url_Parse_SplitsOwnerRepoNumber() =>
        Assert.Equal(("acme", "widgets", 12), PullRequestUrl.Parse(Url));

    [Fact]
    public void Url_Parse_ANumberPastIntMax_IsAGitHubException_NotAnOverflow() =>
        Assert.Throws<GitHubException>(() => PullRequestUrl.Parse("https://github.com/acme/widgets/pull/99999999999"));

    [Fact]
    public async Task Get_DraftOpenWithPassingChecks()
    {
        var (api, graph, _) = Make();
        const string rollup = """
            [{"__typename":"CheckRun","status":"COMPLETED","conclusion":"SUCCESS"},
             {"__typename":"StatusContext","state":"SUCCESS"},
             {"__typename":"CheckRun","status":"COMPLETED","conclusion":"SKIPPED"}]
            """;
        graph.Then(Get(Pr(draft: true, rollup: rollup)));

        var s = await api.GetAsync(Url);

        Assert.Equal(new PullRequestStatus(Url, 12, "Fix it", "open", true, "passing")
            { Base = "main", Head = "fyrd/a", ChecksPassed = 3, Mergeable = "mergeable" }, s with { FailingChecks = [] });
        Assert.Empty(s.FailingChecks);
        Assert.False(s.CanMerge);
        var req = Assert.Single(graph.Requests);
        Assert.Equal("https://api.github.com/graphql", req.Request.RequestUri!.ToString());
        Assert.Equal("Bearer " + Secret, req.Request.Headers.Authorization!.ToString());
        using var body = JsonDocument.Parse(req.Body!);
        Assert.Equal("acme", body.RootElement.GetProperty("variables").GetProperty("owner").GetString());
        Assert.Equal(12, body.RootElement.GetProperty("variables").GetProperty("number").GetInt32());
    }

    [Theory]
    [InlineData("""[{"__typename":"CheckRun","status":"COMPLETED","conclusion":"FAILURE"},{"__typename":"CheckRun","status":"COMPLETED","conclusion":"SUCCESS"}]""", "failing")]
    [InlineData("""[{"__typename":"CheckRun","status":"COMPLETED","conclusion":"TIMED_OUT"}]""", "failing")]
    [InlineData("""[{"__typename":"CheckRun","status":"COMPLETED","conclusion":"STARTUP_FAILURE"}]""", "failing")]
    [InlineData("""[{"__typename":"CheckRun","status":"COMPLETED","conclusion":"STALE"}]""", "failing")]
    [InlineData("""[{"__typename":"StatusContext","state":"ERROR"}]""", "failing")]
    [InlineData("""[{"__typename":"CheckRun","status":"IN_PROGRESS","conclusion":null},{"__typename":"CheckRun","status":"COMPLETED","conclusion":"SUCCESS"}]""", "pending")]
    [InlineData("""[{"__typename":"StatusContext","state":"PENDING"}]""", "pending")]
    [InlineData("""[{"__typename":"StatusContext","state":"EXPECTED"}]""", "pending")]
    [InlineData("""[{"__typename":"CheckRun","status":"COMPLETED","conclusion":"FAILURE"},{"__typename":"CheckRun","status":"QUEUED","conclusion":null}]""", "failing")]
    [InlineData("[]", "none")]
    public void ParseStatus_ChecksRollup(string rollup, string expected)
    {
        using var doc = JsonDocument.Parse(Pr(rollup: rollup));
        Assert.Equal(expected, GitHubApi.ParseStatus(Url, doc.RootElement).Checks);
    }

    [Theory]
    [InlineData("OPEN", false, "passing", true)]
    [InlineData("OPEN", false, "none", true)]
    [InlineData("OPEN", false, "pending", false)]
    [InlineData("OPEN", false, "failing", false)]
    [InlineData("OPEN", true, "passing", false)]
    [InlineData("MERGED", false, "passing", false)]
    [InlineData("CLOSED", false, "passing", false)]
    public void CanMerge_OnlyReadyOpenWithGreenChecks(string state, bool draft, string checks, bool expected)
    {
        var rollup = checks switch
        {
            "passing" => """[{"__typename":"CheckRun","status":"COMPLETED","conclusion":"SUCCESS"}]""",
            "pending" => """[{"__typename":"CheckRun","status":"IN_PROGRESS"}]""",
            "failing" => """[{"__typename":"CheckRun","status":"COMPLETED","conclusion":"FAILURE"}]""",
            _ => "[]",
        };
        using var doc = JsonDocument.Parse(Pr(state, draft, rollup));
        Assert.Equal(expected, GitHubApi.ParseStatus(Url, doc.RootElement).CanMerge);
    }

    [Fact]
    public async Task Get_MergedPr_IsMerged()
    {
        var (api, graph, _) = Make();
        graph.Then(Get(Pr("MERGED")));
        Assert.Equal("merged", (await api.GetAsync(Url)).State);
    }

    [Fact]
    public async Task List_JoinsTheLightAndHeavyAliasesByUrl_AndReadsTheDefaultBranch()
    {
        var (api, graph, _) = Make();
        graph.Then("""
            {"data":{"repository":{"defaultBranchRef":{"name":"main"},
              "all":{"nodes":[
                {"url":"https://github.com/acme/widgets/pull/13","number":13,"title":"old","state":"MERGED","isDraft":false,"headRefName":"fyrd/a","baseRefName":"main","isCrossRepository":false,"createdAt":"2026-11-01T09:30:00Z"},
                {"url":"https://github.com/acme/widgets/pull/14","number":14,"title":"new","state":"OPEN","isDraft":true,"headRefName":"fyrd/b","baseRefName":"fyrd/a","isCrossRepository":false},
                {"url":"https://github.com/acme/widgets/pull/16","number":16,"title":"fork","state":"OPEN","isDraft":false,"headRefName":"fyrd/f","baseRefName":"main","isCrossRepository":true},
                {"url":"not a url","headRefName":"fyrd/c"},
                {"url":"https://github.com/acme/widgets/pull/15","headRefName":""}]},
              "open":{"nodes":[
                {"url":"https://github.com/acme/widgets/pull/14","number":14,"title":"new","state":"OPEN","isDraft":true,"headRefName":"fyrd/b","baseRefName":"fyrd/a",
                 "mergeable":"CONFLICTING","commits":{"nodes":[{"commit":{"statusCheckRollup":{"contexts":{"nodes":[{"__typename":"CheckRun","name":"b","status":"COMPLETED","conclusion":"FAILURE","detailsUrl":"https://ci/b"}]}}}}]}}]}}}}
            """);

        var list = await api.ListAsync("acme/widgets");

        Assert.Equal("main", list.DefaultBranch);
        Assert.Equal(["https://github.com/acme/widgets/pull/13", "https://github.com/acme/widgets/pull/14", "https://github.com/acme/widgets/pull/16"],
            list.Prs.Select(p => p.Url));
        var merged = list.Prs[0];
        Assert.Equal(("fyrd/a", "main", "merged", false), (merged.Branch, merged.Base, merged.State, merged.CrossRepository));
        Assert.Equal("none", merged.Status!.Checks); // light: no rollup for non-open PRs
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T09:30:00Z"), merged.CreatedAt);
        Assert.Null(list.Prs[1].CreatedAt);
        var open = list.Prs[1];
        Assert.Equal(("fyrd/b", "fyrd/a", "open"), (open.Branch, open.Base, open.State));
        Assert.Equal("failing", open.Status!.Checks); // heavy: joined by URL
        Assert.Equal("conflicting", open.Status.Mergeable);
        Assert.True(open.Status.Draft);
        Assert.True(list.Prs[2].CrossRepository);
        Assert.Equal([false, false, true], list.Prs.Select(p => p.Partial)); // only an open PR missing from the heavy alias is partial
        using var body = JsonDocument.Parse(Assert.Single(graph.Requests).Body!);
        var q = body.RootElement.GetProperty("query").GetString()!;
        Assert.Contains("all:pullRequests(first:100", q);
        Assert.Contains("isCrossRepository createdAt}", q);
        Assert.Contains("open:pullRequests(first:5,states:OPEN,orderBy:{field:UPDATED_AT,direction:DESC})", q);
        Assert.Contains("defaultBranchRef{name}", q);
    }

    [Fact]
    public async Task List_RejectsAnInvalidRepo_WithoutARequest()
    {
        var (api, graph, _) = Make();
        await Assert.ThrowsAsync<GitHubException>(() => api.ListAsync("--repo=evil"));
        Assert.Empty(graph.Requests);
    }

    [Fact]
    public async Task MarkReady_SendsTheMutationWithTheIdFromAFreshRead()
    {
        var (api, graph, _) = Make();
        graph.Then(Get(Pr(draft: true))).Then("""{"data":{"markPullRequestReadyForReview":{"pullRequest":{"id":"PR_node12","isDraft":false}}}}""");

        await api.MarkReadyAsync(Url);

        Assert.Equal(2, graph.Requests.Count);
        using var read = JsonDocument.Parse(graph.Requests[0].Body!);
        Assert.Contains("pullRequest(number:$number)", read.RootElement.GetProperty("query").GetString());
        using var mutation = JsonDocument.Parse(graph.Requests[1].Body!);
        Assert.Contains("markPullRequestReadyForReview(input:{pullRequestId:$id})", mutation.RootElement.GetProperty("query").GetString());
        Assert.Equal("PR_node12", mutation.RootElement.GetProperty("variables").GetProperty("id").GetString());
    }

    [Fact]
    public async Task Merge_IsAlwaysASquash()
    {
        var (api, _, rest) = Make();
        rest.Then("""{"sha":"abc","merged":true,"message":"Pull Request successfully merged"}""");

        await api.MergeAsync(Url);

        var req = Assert.Single(rest.Requests);
        Assert.Equal(HttpMethod.Put, req.Request.Method);
        Assert.Equal("https://api.github.com/repos/acme/widgets/pulls/12/merge", req.Request.RequestUri!.ToString());
        using var body = JsonDocument.Parse(req.Body!);
        Assert.Equal("squash", body.RootElement.GetProperty("merge_method").GetString());
    }

    [Fact]
    public async Task Merge_WithAHeadSha_PinsTheMergeToIt()
    {
        var (api, _, rest) = Make();
        rest.Then("""{"sha":"abc","merged":true,"message":"ok"}""");
        await api.MergeAsync(Url, "deadbeef");
        using var body = JsonDocument.Parse(Assert.Single(rest.Requests).Body!);
        Assert.Equal("deadbeef", body.RootElement.GetProperty("sha").GetString());
        Assert.Equal("squash", body.RootElement.GetProperty("merge_method").GetString());
    }

    [Fact]
    public async Task Merge_WhenTheHeadMoved_409_SaysSo()
    {
        var (api, _, rest) = Make();
        rest.Then("""{"message":"Head branch was modified. Review and try the merge again."}""", HttpStatusCode.Conflict);
        var e = await Assert.ThrowsAsync<GitHubException>(() => api.MergeAsync(Url, "stale"));
        Assert.Contains("changed after it was checked", e.Message);
    }

    [Fact]
    public void ParseStatus_ReadsTheHeadSha() =>
        Assert.Equal("abc123", Parse(Pr().Replace("\"mergeable\"", "\"headRefOid\":\"abc123\",\"mergeable\"")).HeadSha);

    [Fact]
    public async Task Merge_Refused_BecomesAGitHubException_WithoutTheToken()
    {
        var (api, _, rest) = Make();
        rest.Then("""{"message":"Merge method not allowed"}""", HttpStatusCode.MethodNotAllowed);

        var e = await Assert.ThrowsAsync<GitHubException>(() => api.MergeAsync(Url));

        Assert.StartsWith("GitHub refused the merge:", e.Message);
        Assert.DoesNotContain(Secret, e.Message);
    }

    [Fact]
    public async Task Login_ReadsTheCurrentUser()
    {
        var (api, _, rest) = Make();
        rest.Then("""{"login":"octocat","id":1}""");
        Assert.Equal("octocat", await api.LoginAsync());
        Assert.Equal("https://api.github.com/user", rest.Requests.Single().Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task GraphQlErrors_BecomeAGitHubException()
    {
        var (api, graph, _) = Make();
        graph.Then("""{"errors":[{"type":"UNPROCESSABLE","message":"Field 'x' doesn't exist"}]}""");

        var e = await Assert.ThrowsAsync<GitHubException>(() => api.GetAsync(Url));

        Assert.Equal("GitHub refused the request: Field 'x' doesn't exist", e.Message);
    }

    [Fact]
    public async Task GraphQlNotFound_SaysTheTokenLacksAccess()
    {
        var (api, graph, _) = Make();
        graph.Then("""{"data":{"repository":null},"errors":[{"type":"NOT_FOUND","path":["repository"],"message":"Could not resolve to a Repository"}]}""");
        var e = await Assert.ThrowsAsync<GitHubException>(() => api.GetAsync(Url));
        Assert.Equal("GitHub token lacks access to acme/widgets.", e.Message);
        Assert.IsNotType<GitHubPullRequestNotFoundException>(e); // a hidden repo is not proof the PR does not exist
    }

    [Fact]
    public async Task GraphQlNotFound_OnThePullRequest_SaysThePrDoesNotExist()
    {
        var (api, graph, _) = Make();
        graph.Then("""{"data":{"repository":{"pullRequest":null}},"errors":[{"type":"NOT_FOUND","path":["repository","pullRequest"],"message":"Could not resolve to a PullRequest with the number of 12."}]}""");
        var e = await Assert.ThrowsAsync<GitHubPullRequestNotFoundException>(() => api.GetAsync(Url));
        Assert.Equal($"GitHub has no pull request at {Url}.", e.Message);
    }

    [Fact]
    public async Task GraphQlNotFound_InAListing_SaysTheTokenLacksAccess()
    {
        var (api, graph, _) = Make();
        graph.Then("""{"errors":[{"type":"NOT_FOUND","path":["repository"],"message":"Could not resolve to a Repository"}]}""");
        var e = await Assert.ThrowsAsync<GitHubException>(() => api.ListAsync("acme/widgets"));
        Assert.Equal("GitHub token lacks access to acme/widgets.", e.Message);
    }

    [Fact]
    public async Task RateLimitResponse_BecomesGitHubRateLimitException_WithTheResetTime()
    {
        var (api, graph, _) = Make();
        graph.Then("{}", HttpStatusCode.Forbidden, new() { ["x-ratelimit-remaining"] = "0", ["x-ratelimit-reset"] = "1793620800" });

        var e = await Assert.ThrowsAsync<GitHubRateLimitException>(() => api.ListAsync("acme/widgets"));

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793620800), e.ResetAt);
    }

    [Fact]
    public async Task GraphQlRateLimited_BecomesGitHubRateLimitException()
    {
        var (api, graph, _) = Make();
        graph.Then("""{"errors":[{"type":"RATE_LIMITED","message":"API rate limit exceeded"}]}""");
        await Assert.ThrowsAsync<GitHubRateLimitException>(() => api.GetAsync(Url));
    }

    [Fact]
    public async Task ForbiddenWithoutExhaustedLimit_IsAnOrdinaryError()
    {
        var (api, graph, _) = Make();
        graph.Then("{}", HttpStatusCode.Forbidden, new() { ["x-ratelimit-remaining"] = "42" });
        var e = await Assert.ThrowsAsync<GitHubException>(() => api.GetAsync(Url));
        Assert.IsNotType<GitHubRateLimitException>(e);
    }

    [Fact]
    public async Task Unauthorized_SaysTheTokenIsBad()
    {
        var (api, graph, _) = Make();
        graph.Then("{}", HttpStatusCode.Unauthorized);
        var e = await Assert.ThrowsAsync<GitHubException>(() => api.GetAsync(Url));
        Assert.Contains("bad or expired", e.Message);
        Assert.DoesNotContain(Secret, e.Message);
    }

    [Fact]
    public async Task NoToken_ThrowsWithoutAnyRequest()
    {
        var (api, graph, rest) = Make(token: null);

        Assert.False(api.Configured);
        Assert.Null(await api.LoginAsync());
        await Assert.ThrowsAsync<GitHubNotConfiguredException>(() => api.ListAsync("acme/widgets"));
        await Assert.ThrowsAsync<GitHubNotConfiguredException>(() => api.GetAsync(Url));
        await Assert.ThrowsAsync<GitHubNotConfiguredException>(() => api.MarkReadyAsync(Url));
        await Assert.ThrowsAsync<GitHubNotConfiguredException>(() => api.MergeAsync(Url));
        Assert.Empty(graph.Requests);
        Assert.Empty(rest.Requests);
    }

    [Fact]
    public void TheToken_NeverAppearsInItsOwnText()
    {
        var token = new GitHubToken(Secret);
        Assert.DoesNotContain(Secret, token.ToString());
        Assert.DoesNotContain(Secret, new GitHubNotConfiguredException().Message);
    }

    [Fact]
    public void TheToken_PrefersGhTokenOverGithubToken()
    {
        var (gh, github) = (Environment.GetEnvironmentVariable("GH_TOKEN"), Environment.GetEnvironmentVariable("GITHUB_TOKEN"));
        try
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", null);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", " fallback ");
            Assert.Equal("fallback", GitHubToken.FromEnvironment().Value);
            Environment.SetEnvironmentVariable("GH_TOKEN", "primary");
            Assert.Equal("primary", GitHubToken.FromEnvironment().Value);
            Environment.SetEnvironmentVariable("GH_TOKEN", "");
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", "");
            Assert.False(GitHubToken.FromEnvironment().Present);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", gh);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", github);
        }
    }

    private static PullRequestStatus Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return GitHubApi.ParseStatus(Url, doc.RootElement);
    }

    private const string MixedRollup = """
        [{"__typename":"CheckRun","name":"build","status":"COMPLETED","conclusion":"SUCCESS","detailsUrl":"https://ci/build"},
         {"__typename":"CheckRun","name":"lint","status":"COMPLETED","conclusion":"FAILURE","detailsUrl":"https://ci/lint"},
         {"__typename":"StatusContext","context":"deploy","state":"FAILURE","targetUrl":"https://ci/deploy"},
         {"__typename":"CheckRun","name":"e2e","status":"IN_PROGRESS","conclusion":null,"detailsUrl":"https://ci/e2e"},
         {"__typename":"StatusContext","context":"cov","state":"PENDING","targetUrl":null},
         {"__typename":"CheckRun","name":"docs","status":"COMPLETED","conclusion":"SKIPPED","detailsUrl":"https://ci/docs"},
         {"__typename":"StatusContext","context":"ok","state":"SUCCESS","targetUrl":null}]
        """;

    [Fact]
    public void ParseStatus_CountsChecks_AndKeepsTheFirstFailingOne()
    {
        var s = Parse(Pr(rollup: MixedRollup));

        Assert.Equal("failing", s.Checks);
        Assert.Equal((3, 2, 2), (s.ChecksPassed, s.ChecksFailed, s.ChecksPending));
        Assert.Equal(new FailingCheck("lint", "https://ci/lint"), s.FailingCheck);
        Assert.Equal([new FailingCheck("lint", "https://ci/lint"), new FailingCheck("deploy", "https://ci/deploy")], s.FailingChecks);
    }

    [Fact]
    public void ParseStatus_AFailingStatusContext_NamesItByContextAndTargetUrl()
    {
        var s = Parse(Pr(rollup: """[{"__typename":"StatusContext","context":"deploy","state":"ERROR","targetUrl":"https://ci/deploy"}]"""));
        Assert.Equal(new FailingCheck("deploy", "https://ci/deploy"), s.FailingCheck);
    }

    [Fact]
    public void ParseStatus_NoFailure_NoFailingCheck() =>
        Assert.Null(Parse(Pr(rollup: """[{"__typename":"CheckRun","name":"b","status":"COMPLETED","conclusion":"SUCCESS"}]""")).FailingCheck);

    private static string WithReview(string reviewDecision, string latest) =>
        Pr().Replace("\"mergeable\"", $"\"reviewDecision\":{reviewDecision},\"latestReviews\":{{\"nodes\":{latest}}},\"mergeable\"");

    [Theory]
    [InlineData("\"APPROVED\"", """[{"state":"APPROVED"}]""", "approved")]
    [InlineData("\"CHANGES_REQUESTED\"", """[{"state":"CHANGES_REQUESTED"}]""", "changes_requested")]
    [InlineData("null", """[{"state":"COMMENTED"}]""", "commented")]
    [InlineData("\"REVIEW_REQUIRED\"", """[{"state":"COMMENTED"}]""", "commented")]
    [InlineData("\"REVIEW_REQUIRED\"", "[]", "none")]
    [InlineData("null", "[]", "none")]
    public void ParseStatus_Review_UsesTheDecisionWhenSet_ElseTheLatestReviews(string decision, string latest, string expected) =>
        Assert.Equal(expected, Parse(WithReview(decision, latest)).Review);

    [Theory]
    [InlineData("MERGEABLE", "mergeable")]
    [InlineData("CONFLICTING", "conflicting")]
    [InlineData("UNKNOWN", "unknown")]
    public void ParseStatus_Mergeable(string value, string expected) =>
        Assert.Equal(expected, Parse(Pr(mergeable: value)).Mergeable);

    [Fact]
    public void ParseStatus_MergeStateIsLowerCased_AndBaseAndHeadAreRead()
    {
        var s = Parse(Pr().Replace("\"mergeable\"", "\"mergeStateStatus\":\"HAS_HOOKS\",\"mergeable\""));
        Assert.Equal("has_hooks", s.MergeState);
        Assert.Equal(("main", "fyrd/a"), (s.Base, s.Head));
        Assert.Equal("unknown", Parse(Pr()).MergeState);
    }

    [Fact]
    public void CanMerge_IsFalseWhenConflicting_ButNotGatedOnBlocked()
    {
        Assert.False(Parse(Pr(mergeable: "CONFLICTING")).CanMerge);
        var blocked = Parse(Pr().Replace("\"mergeable\"", "\"mergeStateStatus\":\"BLOCKED\",\"mergeable\""));
        Assert.Equal("blocked", blocked.MergeState);
        Assert.True(blocked.CanMerge);
    }

    [Fact]
    public void ParseStatus_ReadsTheForkFlagAndTheDefaultBranch()
    {
        var s = Parse(Pr().Replace("\"mergeable\"", "\"isCrossRepository\":true,\"baseRepository\":{\"defaultBranchRef\":{\"name\":\"trunk\"}},\"mergeable\""));
        Assert.True(s.CrossRepository);
        Assert.Equal("trunk", s.DefaultBranch);
        var plain = Parse(Pr());
        Assert.False(plain.CrossRepository);
        Assert.Equal("", plain.DefaultBranch);
    }
}

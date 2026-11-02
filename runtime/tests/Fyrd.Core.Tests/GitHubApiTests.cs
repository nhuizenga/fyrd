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

        Assert.Equal(new PullRequestStatus(Url, 12, "Fix it", "open", true, "passing"), s);
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
    public async Task List_ParsesTheAllAlias_SkippingBadEntries()
    {
        var (api, graph, _) = Make();
        graph.Then("""
            {"data":{"repository":{"defaultBranchRef":{"name":"main"},
              "all":{"nodes":[
                {"url":"https://github.com/acme/widgets/pull/13","headRefName":"fyrd/a","state":"MERGED"},
                {"url":"https://github.com/acme/widgets/pull/14","headRefName":"fyrd/b","state":"OPEN"},
                {"url":"not a url","headRefName":"fyrd/c"},
                {"url":"https://github.com/acme/widgets/pull/15","headRefName":""}]},
              "open":{"nodes":[]}}}}
            """);

        var list = await api.ListAsync("acme/widgets");

        Assert.Equal([new PullRequestHead("https://github.com/acme/widgets/pull/13", "fyrd/a"),
                      new PullRequestHead("https://github.com/acme/widgets/pull/14", "fyrd/b")], list);
        using var body = JsonDocument.Parse(Assert.Single(graph.Requests).Body!);
        var q = body.RootElement.GetProperty("query").GetString()!;
        Assert.Contains("all:pullRequests(first:100", q);
        Assert.Contains("open:pullRequests(first:50,states:OPEN)", q);
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
        graph.Then("""{"errors":[{"type":"NOT_FOUND","message":"Could not resolve to a Repository"}]}""");
        var e = await Assert.ThrowsAsync<GitHubException>(() => api.GetAsync(Url));
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
}

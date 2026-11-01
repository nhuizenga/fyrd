using Fyrd.Core.Hub;

namespace Fyrd.Core.Tests;

public class GhPullRequestTests
{
    private const string Url = "https://github.com/acme/widgets/pull/12";

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
    public void Parse_DraftOpenWithPassingChecks()
    {
        var s = GhCli.Parse(Url, """
            {"number":12,"title":"Fix it","state":"OPEN","isDraft":true,
             "statusCheckRollup":[{"__typename":"CheckRun","status":"COMPLETED","conclusion":"SUCCESS"},
                                  {"__typename":"StatusContext","state":"SUCCESS"},
                                  {"__typename":"CheckRun","status":"COMPLETED","conclusion":"SKIPPED"}]}
            """);
        Assert.Equal(new PullRequestStatus(Url, 12, "Fix it", "open", true, "passing"), s);
        Assert.False(s.CanMerge);
    }

    [Theory]
    [InlineData("""[{"status":"COMPLETED","conclusion":"FAILURE"},{"status":"COMPLETED","conclusion":"SUCCESS"}]""", "failing")]
    [InlineData("""[{"state":"ERROR"}]""", "failing")]
    [InlineData("""[{"status":"IN_PROGRESS","conclusion":""},{"status":"COMPLETED","conclusion":"SUCCESS"}]""", "pending")]
    [InlineData("""[{"state":"PENDING"}]""", "pending")]
    [InlineData("""[{"status":"COMPLETED","conclusion":"FAILURE"},{"status":"QUEUED","conclusion":""}]""", "failing")]
    [InlineData("[]", "none")]
    public void Parse_ChecksRollup(string rollup, string expected) =>
        Assert.Equal(expected, GhCli.Parse(Url, $$"""{"number":1,"title":"t","state":"OPEN","isDraft":false,"statusCheckRollup":{{rollup}}}""").Checks);

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
            "passing" => """[{"status":"COMPLETED","conclusion":"SUCCESS"}]""",
            "pending" => """[{"status":"IN_PROGRESS"}]""",
            "failing" => """[{"status":"COMPLETED","conclusion":"FAILURE"}]""",
            _ => "[]",
        };
        var s = GhCli.Parse(Url, $$"""{"number":1,"title":"t","state":"{{state}}","isDraft":{{draft.ToString().ToLowerInvariant()}},"statusCheckRollup":{{rollup}}}""");
        Assert.Equal(expected, s.CanMerge);
    }
}

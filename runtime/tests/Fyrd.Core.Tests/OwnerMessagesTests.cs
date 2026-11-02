using Fyrd.Core.CloudSessions;

namespace Fyrd.Core.Tests;

public class OwnerMessagesTests
{
    [Fact]
    public void Args_QueueOnStdin_NoMessageOnCommandLine()
    {
        Assert.Equal(["-p", "--cloud", "session_abc", "--output-format", "json"],
            ClaudeCliOwnerMessages.Args("session_abc"));
    }

    [Theory]
    [InlineData("-p")]
    [InlineData("session_abc --x")]
    [InlineData("https://claude.ai/code/session_abc")]
    public void Args_RejectsAnythingButASessionId(string id)
    {
        Assert.Throws<ArgumentException>(() => ClaudeCliOwnerMessages.Args(id));
    }

    [Fact]
    public void CheckResult_OkJson_Passes()
    {
        ClaudeCliOwnerMessages.CheckResult(0,
            """{"ok":true,"session_id":"session_abc","url":"https://claude.ai/code/session_abc?from=cli&m=0"}""",
            "SessionEnd hook [bash x] failed: Hook cancelled");
    }

    [Fact]
    public void CheckResult_NotOk_ThrowsTheCliError()
    {
        var e = Assert.Throws<OwnerMessageException>(() => ClaudeCliOwnerMessages.CheckResult(1,
            """{"ok":false,"session_id":"session_abc","error":"cloud session session_abc is archived and cannot accept new messages"}""",
            "Error: failed to send message"));
        Assert.Equal("cloud session session_abc is archived and cannot accept new messages", e.Message);
    }

    [Fact]
    public void CheckResult_NoJson_ThrowsStderr()
    {
        var e = Assert.Throws<OwnerMessageException>(() =>
            ClaudeCliOwnerMessages.CheckResult(1, "", "Error: not logged in\n"));
        Assert.Equal("Error: not logged in", e.Message);
    }

    [Fact]
    public void CheckResult_ExitZeroWithoutOk_Throws()
    {
        Assert.Throws<OwnerMessageException>(() => ClaudeCliOwnerMessages.CheckResult(0, "hello", ""));
    }
}

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

    [Fact]
    public async Task SendAsync_CliExitsBeforeReadingStdin_SurfacesTheCliError()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = Path.Combine(Path.GetTempPath(), "fyrd-fakeclaude-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var script = Path.Combine(dir, "claude");
        File.WriteAllText(script, "#!/bin/sh\necho 'Error: not logged in' >&2\nexit 1\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", dir + Path.PathSeparator + oldPath);
        try
        {
            // A message large enough to overflow the pipe buffer, so the write hits the closed pipe.
            var e = await Assert.ThrowsAsync<OwnerMessageException>(() =>
                new ClaudeCliOwnerMessages().SendAsync("session_abc", new string('x', 1_000_000)));
            Assert.Equal("Error: not logged in", e.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task SendAsync_CallerCancels_KillsTheChildAndRethrowsCancellation()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = Path.Combine(Path.GetTempPath(), "fyrd-fakeclaude-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var script = Path.Combine(dir, "claude");
        File.WriteAllText(script, "#!/bin/sh\ncat >/dev/null\nsleep 30\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", dir + Path.PathSeparator + oldPath);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new ClaudeCliOwnerMessages().SendAsync("session_abc", "hi", cts.Token));
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
            Directory.Delete(dir, true);
        }
    }
}

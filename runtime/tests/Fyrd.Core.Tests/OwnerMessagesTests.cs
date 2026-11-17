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

    // ---- Creating a session ----

    [Fact]
    public void CreateArgs_NewSessionNamed_PromptOnStdin()
    {
        Assert.Equal(["-p", "--cloud", "-n", "Fyrd Controller", "--output-format", "json"],
            ClaudeCliOwnerMessages.CreateArgs("Fyrd Controller"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("-p")]
    [InlineData("a\nb")]
    public void CreateArgs_RejectsUnusableNames(string name)
    {
        Assert.Throws<ArgumentException>(() => ClaudeCliOwnerMessages.CreateArgs(name));
    }

    [Theory]
    [InlineData("session_abc", "session_abc")]
    [InlineData("cse_abc", "session_abc")]
    [InlineData("https://claude.ai/code/session_abc?from=cli&m=0", "session_abc")]
    [InlineData("nothing here", null)]
    [InlineData("", null)]
    public void FindSessionId_NormalisesToSessionPrefix(string text, string? expected)
    {
        Assert.Equal(expected, ClaudeCliOwnerMessages.FindSessionId(text));
    }

    [Theory]
    [InlineData("Cloud session: session_abc (https://claude.ai/code/session_abc)", "session_abc")]
    [InlineData("Cloud session: cse_abc (https://claude.ai/code/session_abc)", "session_abc")]
    [InlineData("Error: cloud session session_abc is archived", null)]
    [InlineData("SessionEnd hook failed", null)]
    public void FindCreatedNote_OnlyTheCreatedLine(string line, string? expected)
    {
        Assert.Equal(expected, ClaudeCliOwnerMessages.FindCreatedNote(line));
    }

    [Fact]
    public void ParseCreated_ResultWithCloudSessionId()
    {
        Assert.Equal("session_abc", ClaudeCliOwnerMessages.ParseCreated(0,
            """{"type":"result","subtype":"success","is_error":false,"result":"Ready.","session_id":"session_abc","session_url":"https://claude.ai/code/session_abc"}""",
            ""));
    }

    [Fact]
    public void ParseCreated_SessionUrlOnly()
    {
        Assert.Equal("session_abc", ClaudeCliOwnerMessages.ParseCreated(0,
            """{"type":"result","session_url":"https://claude.ai/code/session_abc"}""", ""));
    }

    [Fact]
    public void ParseCreated_OkShape()
    {
        Assert.Equal("session_abc", ClaudeCliOwnerMessages.ParseCreated(0,
            """{"ok":true,"session_id":"cse_abc","title":"Fyrd Controller","url":"https://claude.ai/code/session_abc?from=cli&m=0"}""", ""));
    }

    [Fact]
    public void ParseCreated_TurnErroredButSessionExists_ReturnsId()
    {
        Assert.Equal("session_abc", ClaudeCliOwnerMessages.ParseCreated(1,
            """{"type":"result","is_error":true,"session_id":"session_abc"}""", ""));
    }

    [Fact]
    public void ParseCreated_NotOk_ThrowsTheCliError()
    {
        var e = Assert.Throws<OwnerMessageException>(() => ClaudeCliOwnerMessages.ParseCreated(1,
            """{"ok":false,"error":"Unable to create cloud session"}""", "Error: x"));
        Assert.Equal("Unable to create cloud session", e.Message);
    }

    [Fact]
    public void ParseCreated_NoJson_FallsBackToTheStderrNote()
    {
        Assert.Equal("session_abc", ClaudeCliOwnerMessages.ParseCreated(0, "",
            "hook noise\nCloud session: session_abc (https://claude.ai/code/session_abc)\n"));
    }

    [Fact]
    public void ParseCreated_NothingFound_ThrowsStderrOrExitCode()
    {
        var e = Assert.Throws<OwnerMessageException>(() => ClaudeCliOwnerMessages.ParseCreated(1, "", "Error: not logged in\n"));
        Assert.Equal("Error: not logged in", e.Message);
        e = Assert.Throws<OwnerMessageException>(() => ClaudeCliOwnerMessages.ParseCreated(3, "", ""));
        Assert.Contains("3", e.Message);
    }

    /// <summary>Runs <paramref name="body"/> with a fake <c>claude</c> shell script first on PATH.</summary>
    private static async Task WithFakeClaude(string script, Func<string, Task> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fyrd-fakeclaude-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "claude");
        File.WriteAllText(path, "#!/bin/sh\n" + script);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", dir + Path.PathSeparator + oldPath);
        try { await body(dir); }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task CreateAsync_PassesArgsAndPromptOnStdin_ReadsFinalJson()
    {
        if (OperatingSystem.IsWindows()) return;
        await WithFakeClaude("""
            d=$(dirname "$0")
            printf '%s\n' "$@" > "$d/args"
            cat > "$d/stdin"
            echo '{"type":"result","is_error":false,"session_id":"session_new1","session_url":"https://claude.ai/code/session_new1"}'
            """, async dir =>
        {
            var id = await new ClaudeCliOwnerMessages().CreateAsync("Fyrd Controller", "You are the Fyrd Controller.\n\nline two");
            Assert.Equal("session_new1", id);
            Assert.Equal("-p\n--cloud\n-n\nFyrd Controller\n--output-format\njson\n", File.ReadAllText(Path.Combine(dir, "args")));
            Assert.Equal("You are the Fyrd Controller.\n\nline two", File.ReadAllText(Path.Combine(dir, "stdin")));
        });
    }

    [Fact]
    public async Task CreateAsync_ReturnsOnTheStderrNote_WithoutWaitingForTheTurn()
    {
        if (OperatingSystem.IsWindows()) return;
        await WithFakeClaude("""
            cat >/dev/null
            echo 'Cloud session: session_new2 (https://claude.ai/code/session_new2)' >&2
            sleep 20
            """, async _ =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var id = await new ClaudeCliOwnerMessages().CreateAsync("Fyrd Controller", "hi");
            Assert.Equal("session_new2", id);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
        });
    }

    [Fact]
    public async Task CreateAsync_CliFails_SurfacesTheCliError()
    {
        if (OperatingSystem.IsWindows()) return;
        await WithFakeClaude("echo 'Error: not logged in' >&2\nexit 1\n", async _ =>
        {
            var e = await Assert.ThrowsAsync<OwnerMessageException>(() =>
                new ClaudeCliOwnerMessages().CreateAsync("Fyrd Controller", new string('x', 1_000_000)));
            Assert.Equal("Error: not logged in", e.Message);
        });
    }

    [Fact]
    public async Task CreateAsync_CallerCancels_KillsTheChildAndRethrowsCancellation()
    {
        if (OperatingSystem.IsWindows()) return;
        await WithFakeClaude("cat >/dev/null\nsleep 30\n", async _ =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new ClaudeCliOwnerMessages().CreateAsync("Fyrd Controller", "hi", cts.Token));
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
        });
    }
}

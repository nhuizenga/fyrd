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

    /// <summary>What <c>claude --cloud "controller"</c> printed on the owner's Windows machine (CLI 2.1.326).</summary>
    private const string ProbeOutput =
        "Created cloud session: Controller\r\n" +
        "View: https://claude.ai/code/session_01SQSYWZZdNyXRzob9SmbNhQ?from=cli&m=0\r\n" +
        "Resume with: claude --teleport session_01SQSYWZZdNyXRzob9SmbNhQ\r\n" +
        "This folder is not a git repository, so the cloud session starts empty: nothing from this folder is copied into it.\r\n";

    [Fact]
    public void CreateCommand_InteractiveCloudWithDescription()
    {
        Assert.Equal(["claude", "--cloud", "controller"], ClaudeCliOwnerMessages.CreateCommand("controller"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("-p")]
    [InlineData("a\"b")]
    [InlineData("x & calc")]
    [InlineData("line\nbreak")]
    public void CreateCommand_RejectsUnusableDescriptions(string description)
    {
        Assert.Throws<ArgumentException>(() => ClaudeCliOwnerMessages.CreateCommand(description));
    }

    [Theory]
    [InlineData("Cloud session: session_abc (https://claude.ai/code/session_abc)", "session_abc")]
    [InlineData("https://claude.ai/code/cse_abc?from=cli", "session_abc")]
    [InlineData("nothing here", null)]
    public void FindSessionId_NormalisesToSessionPrefix(string text, string? expected)
    {
        Assert.Equal(expected, ClaudeCliOwnerMessages.FindSessionId(text));
    }

    [Fact]
    public void FindCreatedId_TheOwnersProbeOutput()
    {
        Assert.Equal("session_01SQSYWZZdNyXRzob9SmbNhQ", ClaudeCliOwnerMessages.FindCreatedId(ProbeOutput));
    }

    [Fact]
    public void FindCreatedId_WithTerminalEscapes()
    {
        var output = "\u001b[?25l\u001b[2J\u001b[H\u001b]0;claude\u0007\u001b[32mCreated cloud session:\u001b[39m Controller\r\n" +
                     "\u001b[2mView:\u001b[22m \u001b]8;;https://claude.ai/code/session_X9?from=cli&m=0\u001b\\" +
                     "https://claude.ai/code/session_X9?from=cli&m=0\u001b]8;;\u001b\\\r\n\u001b[?25h";
        Assert.Equal("session_X9", ClaudeCliOwnerMessages.FindCreatedId(output));
    }

    [Fact]
    public void FindCreatedId_ResumeLineAlone()
    {
        Assert.Equal("session_r1", ClaudeCliOwnerMessages.FindCreatedId("Resume with: claude --teleport session_r1\n"));
    }

    [Fact]
    public void FindCreatedId_LinesRunTogether_UrlEndsAtQueryString()
    {
        // What stripping leaves when a pseudo terminal lays lines out with cursor moves it doesn't map to line breaks.
        var output = "Created cloud session: ControllerView: https://claude.ai/code/session_01CupAbc?from=cli&m=0" +
                     "Resume with: claude --teleport session_01CupAbcThis folder is not a git repository";
        Assert.Equal("session_01CupAbc", ClaudeCliOwnerMessages.FindCreatedId(output));
    }

    [Fact]
    public void FindCreatedId_CursorPositionedLines()
    {
        var output = "\u001b[1;1HCreated cloud session: Controller\u001b[2;1HView: https://claude.ai/code/session_01Cup9" +
                     "\u001b[3;1HResume with: claude --teleport session_01Cup9\u001b[4;1HThis folder is not a git repository";
        Assert.Equal("session_01Cup9", ClaudeCliOwnerMessages.FindCreatedId(output));
        Assert.Contains("Resume with: claude --teleport session_01Cup9", ClaudeCliOwnerMessages.PlainLines(output));
    }

    [Fact]
    public void FindCreatedId_TeleportOnly_NeedsWhiteSpaceAfterTheId()
    {
        Assert.Equal("session_t1", ClaudeCliOwnerMessages.FindCreatedId("Resume with: claude --teleport session_t1\r\n"));
        Assert.Null(ClaudeCliOwnerMessages.FindCreatedId("Resume with: claude --teleport session_t1"));
    }

    [Fact]
    public void FindCreatedId_UrlStillBeingPrinted_Null()
    {
        Assert.Null(ClaudeCliOwnerMessages.FindCreatedId("Created cloud session: Controller\r\nView: https://claude.ai/code/session_01SQ"));
    }

    [Fact]
    public void PlainLines_StripsDcsAndApcStrings()
    {
        var output = "\u001bP>|xterm(1)\u001b\\\u001b_apc payload\u001b\\\u001b^pm\u0007Created cloud session: Controller";
        Assert.Equal(["Created cloud session: Controller"], ClaudeCliOwnerMessages.PlainLines(output));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Created cloud session: Controller\r\n")]
    [InlineData("Error: --cloud \"controller\" is not a cloud session ID or URL (session_ or cse_)")]
    public void FindCreatedId_NotYet_Null(string output)
    {
        Assert.Null(ClaudeCliOwnerMessages.FindCreatedId(output));
    }

    [Fact]
    public void FindBlockingPrompt_FolderTrust_SaysWhereToTrust()
    {
        var prompt = ClaudeCliOwnerMessages.FindBlockingPrompt(
            "\u001b[1mQuick safety check:\u001b[22m Is this a project you created or one you trust?\r\n> No, exit\r\n  Yes, I trust this folder",
            @"C:\Users\o\.fyrd\hub\controller-launch");
        Assert.NotNull(prompt);
        Assert.Contains(@"C:\Users\o\.fyrd\hub\controller-launch", prompt);
        Assert.Contains("Yes, I trust this folder", prompt);
        Assert.Null(ClaudeCliOwnerMessages.FindBlockingPrompt(ProbeOutput, "x"));
    }

    [Fact]
    public void ScriptArgs_QuotesTheCommandForUtilLinux_PassesArgvForBsd()
    {
        Assert.Equal(["-qfec", "'claude' '--cloud' 'it'\\''s'", "/dev/null"],
            ScriptTerminalRunner.ScriptArgs(["claude", "--cloud", "it's"], bsd: false));
        Assert.Equal(["-q", "/dev/null", "claude", "--cloud", "controller"],
            ScriptTerminalRunner.ScriptArgs(["claude", "--cloud", "controller"], bsd: true));
    }

    [Fact]
    public void ConPtyCommandLine_QuotesEachArgumentUnderCmd()
    {
        var line = ConPtyTerminalRunner.CommandLine(["claude", "--cloud", "controller"]);
        Assert.EndsWith(" /d /s /c \"\"claude\" \"--cloud\" \"controller\"\"", line);
        Assert.Throws<ArgumentException>(() => ConPtyTerminalRunner.CommandLine(["claude", "a&b"]));
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

    private static bool NoScript => OperatingSystem.IsWindows() || !File.Exists("/usr/bin/script") && !File.Exists("/bin/script");

    [Fact]
    public async Task CreateAsync_RunsInATerminalInTheLaunchDir_ReadsTheId()
    {
        if (NoScript) return;
        await WithFakeClaude("""
            d=$(dirname "$0")
            printf '%s\n' "$@" > "$d/args"
            pwd > "$d/cwd"
            if [ -t 0 ] && [ -t 1 ]; then echo tty > "$d/tty"; fi
            printf 'Created cloud session: Controller\r\n'
            printf 'View: https://claude.ai/code/session_new1?from=cli&m=0\r\n'
            printf 'Resume with: claude --teleport session_new1\r\n'
            """, async dir =>
        {
            var launch = Path.Combine(dir, "launch");
            var id = await new ClaudeCliOwnerMessages().CreateAsync("controller", launch);
            Assert.Equal("session_new1", id);
            Assert.Equal("--cloud\ncontroller\n", File.ReadAllText(Path.Combine(dir, "args")));
            Assert.Equal(launch, File.ReadAllText(Path.Combine(dir, "cwd")).Trim());
            Assert.True(File.Exists(Path.Combine(dir, "tty")), "claude didn't get a terminal.");
        });
    }

    [Fact]
    public async Task CreateAsync_CliKeepsRunningAfterTheId_StopsIt()
    {
        if (NoScript) return;
        await WithFakeClaude("""
            printf 'View: https://claude.ai/code/session_new2?from=cli&m=0\r\n'
            sleep 30
            """, async dir =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Assert.Equal("session_new2", await new ClaudeCliOwnerMessages().CreateAsync("controller", Path.Combine(dir, "l")));
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15));
        });
    }

    [Fact]
    public async Task CreateAsync_FolderTrustPrompt_StopsAndSaysWhatToDo()
    {
        if (NoScript) return;
        await WithFakeClaude("""
            printf 'Quick safety check: Is this a project you created or one you trust?\r\n> No, exit\r\n  Yes, I trust this folder\r\n'
            sleep 30
            """, async dir =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var e = await Assert.ThrowsAsync<OwnerMessageException>(() =>
                new ClaudeCliOwnerMessages().CreateAsync("controller", Path.Combine(dir, "l")));
            Assert.Contains("trust", e.Message);
            Assert.Contains(Path.Combine(dir, "l"), e.Message);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15));
        });
    }

    [Fact]
    public async Task CreateAsync_CliFails_SurfacesWhatItPrinted()
    {
        if (NoScript) return;
        await WithFakeClaude("echo 'Error: Cloud session create failed: Request failed with status code 403' >&2\nexit 1\n", async dir =>
        {
            var e = await Assert.ThrowsAsync<OwnerMessageException>(() =>
                new ClaudeCliOwnerMessages().CreateAsync("controller", Path.Combine(dir, "l")));
            Assert.Contains("status code 403", e.Message);
        });
    }

    [Fact]
    public async Task CreateAsync_CallerCancels_KillsTheChildAndRethrowsCancellation()
    {
        if (NoScript) return;
        await WithFakeClaude("sleep 30\n", async dir =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new ClaudeCliOwnerMessages().CreateAsync("controller", Path.Combine(dir, "l"), cts.Token));
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
        });
    }

    [Fact]
    public async Task CreateAsync_NoIdBeforeTimeout_KillsTheChildAndSaysItMayExist()
    {
        if (NoScript) return;
        var old = ClaudeCliOwnerMessages.CreateTimeout;
        ClaudeCliOwnerMessages.CreateTimeout = TimeSpan.FromMilliseconds(500);
        try
        {
            await WithFakeClaude("sleep 30\n", async dir =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var e = await Assert.ThrowsAsync<OwnerMessageException>(() =>
                    new ClaudeCliOwnerMessages().CreateAsync("controller", Path.Combine(dir, "l")));
                Assert.Contains("may still have been created", e.Message);
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
            });
        }
        finally { ClaudeCliOwnerMessages.CreateTimeout = old; }
    }
}

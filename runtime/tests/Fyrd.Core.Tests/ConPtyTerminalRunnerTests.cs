using System.Diagnostics;
using Fyrd.Core.CloudSessions;

namespace Fyrd.Core.Tests;

/// <summary>
/// Drives the Windows pseudo console runner with a fake <c>claude.cmd</c>. These only run on Windows (elsewhere they return
/// at once); run just them with <c>scripts/test-conpty.ps1</c>, or
/// <c>dotnet test runtime/tests/Fyrd.Core.Tests --filter Category=ConPty</c>.
/// </summary>
[Trait("Category", "ConPty")]
public sealed class ConPtyTerminalRunnerTests
{
    /// <summary>
    /// A fake <c>claude.cmd</c>: records whether its console is redirected (it must not be, or the real CLI refuses to
    /// create a session), prints the CLI's create output, then keeps running and writes <c>done.txt</c> if nothing stops it.
    /// </summary>
    private const string FakeClaude = """
        @echo off
        powershell -NoProfile -NonInteractive -Command "Write-Output ('redirected=' + [Console]::IsInputRedirected + ',' + [Console]::IsOutputRedirected)"
        echo Created cloud session: Controller
        echo View: https://claude.ai/code/session_01ConPty9?from=cli^&m=0
        echo Resume with: claude --teleport session_01ConPty9
        echo This folder is not a git repository, so the cloud session starts empty.
        ping -n 9 127.0.0.1 >nul
        echo done> "%~dp0done.txt"
        """;

    private static async Task WithFakeClaudeCmd(Func<string, Task> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fyrd-conpty-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "claude.cmd"), FakeClaude.Replace("\n", "\r\n"));
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", dir + Path.PathSeparator + oldPath);
        try { await body(dir); }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task Run_GivesTheChildAConsole_ReadsTheId_KillsTheLingeringChild()
    {
        if (!OperatingSystem.IsWindows()) return;
        await WithFakeClaudeCmd(async dir =>
        {
            var launch = Path.Combine(dir, "launch");
            Directory.CreateDirectory(launch);
            var sw = Stopwatch.StartNew();

            var run = await new ConPtyTerminalRunner().RunAsync(ClaudeCliOwnerMessages.CreateCommand("controller"), launch,
                ClaudeCliOwnerMessages.CreateFinished, TimeSpan.FromSeconds(60));

            var lines = ClaudeCliOwnerMessages.PlainLines(run.Output);
            Assert.Contains("redirected=False,False", lines);
            Assert.Equal("session_01ConPty9", ClaudeCliOwnerMessages.FindCreatedId(run.Output, exited: false));
            Assert.Null(run.ExitCode); // stopped by Fyrd, not exited
            Assert.False(run.TimedOut);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), $"Stopping took {sw.Elapsed}.");

            // Killing the job must stop the whole tree: the fake would write done.txt about 8 seconds after it started.
            await Task.Delay(TimeSpan.FromSeconds(12) - sw.Elapsed);
            Assert.False(File.Exists(Path.Combine(dir, "done.txt")), "The fake claude kept running after it was stopped.");
        });
    }

    [Fact]
    public async Task CreateAsync_OnWindows_ReturnsTheIdFromThePseudoConsole()
    {
        if (!OperatingSystem.IsWindows()) return;
        await WithFakeClaudeCmd(async dir =>
        {
            var id = await new ClaudeCliOwnerMessages(new ConPtyTerminalRunner(), TimeSpan.FromSeconds(60))
                .CreateAsync("controller", Path.Combine(dir, "launch"));
            Assert.Equal("session_01ConPty9", id);
        });
    }

    [Fact]
    public async Task Run_ChildExitsByItself_ReportsItsExitCode()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = Path.Combine(Path.GetTempPath(), "fyrd-conpty-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var run = await new ConPtyTerminalRunner().RunAsync(["cmd", "/c", "exit", "3"], dir, _ => false, TimeSpan.FromSeconds(30));
            Assert.Equal(3, run.ExitCode);
            Assert.False(run.TimedOut);
        }
        finally { Directory.Delete(dir, true); }
    }
}

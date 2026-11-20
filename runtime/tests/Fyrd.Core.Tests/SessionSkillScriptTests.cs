using System.Diagnostics;
using Fyrd.Core.Hub;

namespace Fyrd.Core.Tests;

public sealed class SessionSkillScriptTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fyrd-script-" + Guid.NewGuid());

    public SessionSkillScriptTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void Render_IsOneMarkedBlock_ThatInstallsThePluginFromThePublicRepository()
    {
        var script = SessionSkillScript.Render();
        var lines = script.Split('\n');
        Assert.Equal("# >>> fyrd plugin >>>", lines[0]);
        Assert.Equal("# <<< fyrd plugin <<<", lines[^2]);
        Assert.Equal("", lines[^1]);
        Assert.Contains("    claude plugin marketplace add nhuizenga/fyrd", lines);
        Assert.Contains("    claude plugin install fyrd@fyrd --scope user", lines);
        Assert.Equal(") || true", lines[^3]);
        // It holds no skill text, so it is the same block every time.
        Assert.Equal(script, SessionSkillScript.Render());
        Assert.DoesNotContain("exit", script, StringComparison.Ordinal);
    }

    [Fact]
    public void PluginSkillName_IsTheNameClaudeCodeGivesAPluginSkill() =>
        Assert.Equal("fyrd:stacked-pull-requests", SessionSkillScript.PluginSkillName("stacked-pull-requests"));

    /// <summary>A <c>claude</c> on the PATH that logs its arguments and exits with <paramref name="exit"/>.</summary>
    private string FakeClaude(int exit)
    {
        var bin = Path.Combine(_dir, "bin");
        Directory.CreateDirectory(bin);
        var file = Path.Combine(bin, "claude");
        File.WriteAllText(file, $"#!/bin/sh\necho \"$*\" >> '{Path.Combine(_dir, "calls")}'\nexit {exit}\n");
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return bin;
    }

    private (int Exit, string Output) Run(string script, string shell, string path)
    {
        var file = Path.Combine(_dir, "setup-" + Guid.NewGuid().ToString("N") + ".sh");
        File.WriteAllText(file, script);
        var start = new ProcessStartInfo(shell) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(file);
        start.Environment["PATH"] = path;
        using var p = Process.Start(start)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stdout + stderr);
    }

    private static string SystemPath => Environment.GetEnvironmentVariable("PATH") is { Length: > 0 } p ? p : "/usr/bin:/bin";

    /// <summary>The block run for real by a POSIX shell, inside a script with content of its own before and after it.</summary>
    [Theory]
    [InlineData("sh")]
    [InlineData("bash")]
    public void TheBlock_RunsTheClaudeCli_AndLeavesTheRestOfTheScriptRunning(string shell)
    {
        if (OperatingSystem.IsWindows()) return;
        var path = FakeClaude(0) + ":/usr/bin:/bin";
        var (exit, output) = Run("set -eu\necho before\n" + SessionSkillScript.Render() + "echo after $?\n", shell, path);
        Assert.Equal((0, "before\nafter 0\n"), (exit, output));
        Assert.Equal(
            "plugin marketplace add nhuizenga/fyrd\nplugin marketplace update fyrd\nplugin install fyrd@fyrd --scope user\nplugin update fyrd@fyrd\n",
            File.ReadAllText(Path.Combine(_dir, "calls")));
    }

    /// <summary>Every CLI call fails (no network, say): the script around the block still runs, under <c>set -e</c> too.</summary>
    [Theory]
    [InlineData("sh")]
    [InlineData("bash")]
    public void WhenTheCliFails_TheBlockDoesNotStopTheScript(string shell)
    {
        if (OperatingSystem.IsWindows()) return;
        var path = FakeClaude(1) + ":/usr/bin:/bin";
        Assert.Equal((0, "after 0\n"), Run("set -eu\n" + SessionSkillScript.Render() + "echo after $?\n", shell, path));
        Assert.Equal(4, File.ReadAllLines(Path.Combine(_dir, "calls")).Length);
    }

    [Theory]
    [InlineData("sh")]
    [InlineData("bash")]
    public void WithoutTheCli_TheBlockSaysSo_AndTheScriptGoesOn(string shell)
    {
        if (OperatingSystem.IsWindows()) return;
        var empty = Path.Combine(_dir, "empty");
        Directory.CreateDirectory(empty);
        // Only the shell's own builtins: no claude anywhere on the PATH.
        var (exit, output) = Run("set -eu\n" + SessionSkillScript.Render() + "echo after $?\n", Path.Combine("/bin", shell), empty);
        Assert.Equal(0, exit);
        Assert.Contains("the claude command was not found", output, StringComparison.Ordinal);
        // stdout first: the script went on after the block.
        Assert.StartsWith("after 0\n", output, StringComparison.Ordinal);
    }

    /// <summary>Under <c>set -C</c> (noclobber) in the script around it, the option is still on after the block.</summary>
    [Fact]
    public void TheBlock_LeavesTheScriptsOwnOptionsAlone()
    {
        if (OperatingSystem.IsWindows()) return;
        var probe = Path.Combine(_dir, "probe");
        File.WriteAllText(probe, "kept");
        var tail = $"if (echo lost > '{probe}') 2>/dev/null; then echo noclobber off; else echo noclobber on; fi\n";
        Assert.Equal((0, "noclobber on\n"), Run("set -C\n" + SessionSkillScript.Render() + tail, "sh", FakeClaude(0) + ":" + SystemPath));
        Assert.Equal("kept", File.ReadAllText(probe));
    }
}

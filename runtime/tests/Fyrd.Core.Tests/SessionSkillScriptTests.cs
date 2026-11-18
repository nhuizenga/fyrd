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

    private static SessionSkill Skill(string name, string body = "Body.", string description = "Use it.") => new(name, description, ["builder"], body);

    private static readonly SessionSkill[] Two = [Skill("fyrd-b", "# B\n\nBody b."), Skill("fyrd-a", "# A\n\nBody a.")];

    /// <summary>The lines of the block that the shell runs: everything outside the here-documents.</summary>
    private static List<string> Commands(string script)
    {
        var commands = new List<string>();
        string? until = null;
        foreach (var line in script.Split('\n'))
        {
            if (until is not null)
            {
                if (line == until) until = null;
                continue;
            }
            commands.Add(line);
            var at = line.IndexOf("<<'", StringComparison.Ordinal);
            if (at >= 0) until = line[(at + 3)..].TrimEnd('\'');
        }
        Assert.Null(until);
        return commands;
    }

    [Fact]
    public void Render_IsOneMarkedBlock_NamedByTheHashOfTheSkills()
    {
        var script = SessionSkillScript.Render(Two);
        var hash = SessionSkills.Hash(Two);
        var lines = script.Split('\n');
        Assert.Equal($"# >>> fyrd session skills {hash} >>>", lines[0]);
        Assert.Equal("# <<< fyrd session skills <<<", lines[^2]);
        Assert.Equal("", lines[^1]);
        Assert.Single(lines, l => l.StartsWith("# >>> fyrd session skills ", StringComparison.Ordinal));
        Assert.Single(lines, l => l == SessionSkillScript.End);
        Assert.Contains($"  printf '%s\\n' {hash} > \"$fyrd_state/hash\"", lines);
        // The same skills give the same block, in any order.
        Assert.Equal(script, SessionSkillScript.Render([.. Two.Reverse()]));
    }

    [Fact]
    public void Render_WritesEachSkillWithOnlyNameAndDescriptionInItsFrontMatter()
    {
        var script = SessionSkillScript.Render([Skill("fyrd-a", "# A\n\nBody a.", "Stacks: say \"when\" # and \\ how.")]);
        Assert.Contains("  mkdir -p \"$fyrd_skills/fyrd-a\"\n  cat > \"$fyrd_skills/fyrd-a/SKILL.md\" <<'FYRD_SKILL_EOF'\n"
            + "---\nname: fyrd-a\ndescription: \"Stacks: say \\\"when\\\" # and \\\\ how.\"\n---\n\n# A\n\nBody a.\nFYRD_SKILL_EOF\n", script, StringComparison.Ordinal);
        Assert.DoesNotContain("roles", script, StringComparison.Ordinal);
        Assert.Contains("fyrd_claude=\"${FYRD_CLAUDE_DIR:-/root/.claude}\"", script, StringComparison.Ordinal);
        Assert.Contains("fyrd_skills=\"$fyrd_claude/skills\"", script, StringComparison.Ordinal);
        Assert.Equal("/root/.claude/skills", SessionSkillScript.SkillsDir);
    }

    [Fact]
    public void Render_PicksAHereDocumentDelimiterThatIsNoLineOfTheSkill()
    {
        Assert.Equal("FYRD_SKILL_EOF", SessionSkillScript.DelimiterFor("a\n FYRD_SKILL_EOF\nFYRD_SKILL_EOF x\n"));
        Assert.Equal("FYRD_SKILL_EOF_1", SessionSkillScript.DelimiterFor("a\nFYRD_SKILL_EOF\nb\n"));
        Assert.Equal("FYRD_SKILL_EOF_3", SessionSkillScript.DelimiterFor("FYRD_SKILL_EOF_2\nFYRD_SKILL_EOF\nFYRD_SKILL_EOF_1\n"));
        var body = "Before.\nFYRD_SKILL_EOF\nrm -rf /tmp/never\nFYRD_SKILL_EOF_1\nAfter.";
        var script = SessionSkillScript.Render([Skill("fyrd-a", body)]);
        Assert.Contains("<<'FYRD_SKILL_EOF_2'\n", script, StringComparison.Ordinal);
        Assert.Contains("\nAfter.\nFYRD_SKILL_EOF_2\n", script, StringComparison.Ordinal);
        // The body's own lines never reach the shell as commands.
        Assert.DoesNotContain(Commands(script), l => l.Contains("rm -rf /tmp/never", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_CannotFailOrEndTheScriptAroundIt_AndUsesNoNetwork()
    {
        var commands = Commands(SessionSkillScript.Render([.. Two, Skill("fyrd-c", "exit 1\ncurl https://example.com\nset -e")]));
        // One subshell, closed with || true: no option, variable or failure leaves it.
        Assert.Equal("(", commands[4]);
        Assert.Equal("  set +e +u", commands[5]);
        Assert.Equal(") || true", commands[^3]);
        foreach (var line in commands.Where(l => !l.StartsWith('#')))
        {
            Assert.DoesNotMatch(@"\bexit\b|\breturn\b|\bexec\b", line);
            Assert.DoesNotMatch(@"\bset -[a-z]*e|\bcurl\b|\bwget\b|\bgit\b|\bnpm\b|\bapt|https?:", line);
        }
    }

    [Fact]
    public void Render_KeepsAManifest_AndRemovesOnlyFyrdSkillsItListsThatAreGone()
    {
        var script = SessionSkillScript.Render(Two);
        Assert.Contains("  fyrd_keep=\" fyrd-a fyrd-b \"\n", script, StringComparison.Ordinal);
        Assert.Contains("  printf '%s\\n' fyrd-a fyrd-b > \"$fyrd_state/manifest\"\n", script, StringComparison.Ordinal);
        Assert.Contains("      case \"$fyrd_old\" in fyrd-*) ;; *) continue ;; esac\n", script, StringComparison.Ordinal);
        Assert.Contains("      case \"$fyrd_old\" in *[!a-z0-9-]*) continue ;; esac\n", script, StringComparison.Ordinal);
        Assert.Contains("      rm -rf \"$fyrd_skills/$fyrd_old\"\n", script, StringComparison.Ordinal);
        // The only removal is of a name read from the manifest.
        Assert.Single(Commands(script), l => l.Contains("rm ", StringComparison.Ordinal));
        Assert.Contains("  : > \"$fyrd_state/manifest\"\n", SessionSkillScript.Render([]), StringComparison.Ordinal);
    }

    [Fact]
    public void Render_RefusesASkillTheStoreWouldRefuse() =>
        Assert.Throws<ArgumentException>(() => SessionSkillScript.Render([Skill("fyrd-a; rm -rf /")]));

    private (int Exit, string Output) Run(string script, string shell = "sh")
    {
        var file = Path.Combine(_dir, "setup-" + Guid.NewGuid().ToString("N") + ".sh");
        File.WriteAllText(file, script);
        var start = new ProcessStartInfo(shell) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(file);
        start.Environment["FYRD_CLAUDE_DIR"] = Path.Combine(_dir, "claude");
        using var p = Process.Start(start)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }

    private string Installed(params string[] parts) => Path.Combine([_dir, "claude", "skills", .. parts]);

    /// <summary>The block run for real by a POSIX shell, inside a script with content of its own before and after it.</summary>
    [Theory]
    [InlineData("sh")]
    [InlineData("bash")]
    public void TheBlock_Runs_InstallsTheSkills_AndLeavesTheRestOfTheScriptRunning(string shell)
    {
        if (OperatingSystem.IsWindows()) return;
        var tricky = Skill("fyrd-tricky", "# Tricky\n\n$HOME `date` $(id) \\n 'quoted' \"double\"\nFYRD_SKILL_EOF\nexit 7\n\ttabbed", "Tricky: it's \"quoted\".");
        var script = "set -eu\necho before\n" + SessionSkillScript.Render([.. Two, tricky]) + "echo after $?\n";
        var first = Run(script, shell);
        Assert.Equal((0, "before\nafter 0\n"), first);
        Assert.Equal(SessionSkillScript.SkillFile(Two[1]), File.ReadAllText(Installed("fyrd-a", "SKILL.md")));
        Assert.Equal("---\nname: fyrd-tricky\ndescription: \"Tricky: it's \\\"quoted\\\".\"\n---\n\n" + tricky.Body + "\n", File.ReadAllText(Installed("fyrd-tricky", "SKILL.md")));
        var state = Path.Combine(_dir, "claude", "fyrd-session-skills");
        Assert.Equal("fyrd-a\nfyrd-b\nfyrd-tricky\n", File.ReadAllText(Path.Combine(state, "manifest")));
        Assert.Equal(SessionSkills.Hash([.. Two, tricky]) + "\n", File.ReadAllText(Path.Combine(state, "hash")));

        // Running it again changes nothing.
        Assert.Equal(first, Run(script, shell));
        Assert.Equal(["fyrd-a", "fyrd-b", "fyrd-tricky"], Directory.GetDirectories(Installed()).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ALaterBlock_RemovesTheSkillsAnEarlierOneInstalled_AndNothingElse()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal(0, Run(SessionSkillScript.Render(Two)).Exit);
        // Skills that aren't Fyrd's, one that looks like Fyrd's but that no block installed, and a manifest someone tampered with.
        foreach (var other in new[] { "session-start-hook", "fyrd-not-ours", "victim" })
        {
            Directory.CreateDirectory(Installed(other));
            File.WriteAllText(Installed(other, "SKILL.md"), "theirs");
        }
        var manifest = Path.Combine(_dir, "claude", "fyrd-session-skills", "manifest");
        File.AppendAllText(manifest, "session-start-hook\n../skills/victim\nfyrd-../victim\n\nfyrd-b");

        var (exit, output) = Run(SessionSkillScript.Render([Skill("fyrd-a", "# A\n\nNew body a."), Skill("fyrd-c")]));
        Assert.Equal((0, ""), (exit, output));
        Assert.Equal(["fyrd-a", "fyrd-c", "fyrd-not-ours", "session-start-hook", "victim"], Directory.GetDirectories(Installed()).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Contains("New body a.", File.ReadAllText(Installed("fyrd-a", "SKILL.md")), StringComparison.Ordinal);
        Assert.Equal("theirs", File.ReadAllText(Installed("victim", "SKILL.md")));
        Assert.Equal("fyrd-a\nfyrd-c\n", File.ReadAllText(manifest));

        // With no skills left, the block removes what it installed and keeps the rest.
        Assert.Equal(0, Run(SessionSkillScript.Render([])).Exit);
        Assert.Equal(["fyrd-not-ours", "session-start-hook", "victim"], Directory.GetDirectories(Installed()).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal("", File.ReadAllText(manifest));
    }

    [Fact]
    public void TheBlock_DoesNotStopTheScript_WhenItCannotWrite()
    {
        if (OperatingSystem.IsWindows()) return;
        // The skills directory's place is taken by a file, so nothing under it can be made.
        Directory.CreateDirectory(Path.Combine(_dir, "claude"));
        File.WriteAllText(Path.Combine(_dir, "claude", "skills"), "in the way");
        var (exit, output) = Run("set -e\n" + SessionSkillScript.Render(Two) + "echo after\n");
        Assert.Equal(0, exit);
        Assert.Contains("after\n", output, StringComparison.Ordinal);
    }
}

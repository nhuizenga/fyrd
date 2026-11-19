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
            if (at >= 0) until = line[(at + 3)..line.IndexOf('\'', at + 3)];
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
        Assert.Contains($"  [ \"$fyrd_ok\" = 1 ] && printf '%s\\n' {hash} > \"$fyrd_state/hash\"", lines);
        // The same skills give the same block, in any order.
        Assert.Equal(script, SessionSkillScript.Render([.. Two.Reverse()]));
    }

    [Fact]
    public void Render_WritesEachSkillWithOnlyNameAndDescriptionInItsFrontMatter()
    {
        var script = SessionSkillScript.Render([Skill("fyrd-a", "# A\n\nBody a.", "Stacks: say \"when\" # and \\ how.")]);
        Assert.Contains("  mkdir -p \"$fyrd_skills/fyrd-a\" || fyrd_ok=0\n  cat > \"$fyrd_skills/fyrd-a/SKILL.md\" <<'FYRD_SKILL_EOF' || fyrd_ok=0\n"
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
        Assert.Contains("<<'FYRD_SKILL_EOF_2' || fyrd_ok=0\n", script, StringComparison.Ordinal);
        Assert.Contains("\nAfter.\nFYRD_SKILL_EOF_2\n", script, StringComparison.Ordinal);
        // The body's own lines never reach the shell as commands.
        Assert.DoesNotContain(Commands(script), l => l.Contains("rm -rf /tmp/never", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_CannotFailOrEndTheScriptAroundIt_AndUsesNoNetwork()
    {
        var commands = Commands(SessionSkillScript.Render([.. Two, Skill("fyrd-c", "exit 1\ncurl https://example.com\nset -e")]));
        // One subshell, closed with || true: no option, variable or failure leaves it.
        // Inside it, -e, -u and -C (noclobber) of the script around it are off.
        Assert.Equal("(", commands[5]);
        Assert.Equal("  set +e +u +C", commands[6]);
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
        Assert.Contains("  printf '%s\\n' fyrd-a fyrd-b > \"$fyrd_state/manifest\" || fyrd_ok=0\n", script, StringComparison.Ordinal);
        Assert.Contains("      case \"$fyrd_old\" in fyrd-*) ;; *) continue ;; esac\n", script, StringComparison.Ordinal);
        Assert.Contains("      case \"$fyrd_old\" in *[!a-z0-9-]*) continue ;; esac\n", script, StringComparison.Ordinal);
        Assert.Contains("      rm -rf \"$fyrd_skills/$fyrd_old\" || fyrd_ok=0\n", script, StringComparison.Ordinal);
        // The only removals are of a name read from the manifest, and of the block's own hash file.
        Assert.Equal(["  rm -f \"$fyrd_state/hash\" || fyrd_ok=0", "      rm -rf \"$fyrd_skills/$fyrd_old\" || fyrd_ok=0"],
            Commands(script).Where(l => l.Contains("rm ", StringComparison.Ordinal)));
        Assert.Contains("  : > \"$fyrd_state/manifest\" || fyrd_ok=0\n", SessionSkillScript.Render([]), StringComparison.Ordinal);
    }

    [Fact]
    public void Render_RefusesASkillTheStoreWouldRefuse() =>
        Assert.Throws<ArgumentException>(() => SessionSkillScript.Render([Skill("fyrd-a; rm -rf /")]));

    /// <summary>
    /// The whole block for one small skill, pinned. The hash names the skills and <see cref="SessionSkillScript.TemplateVersion"/>,
    /// not the shell text around them, so a change to that text must raise the version, or an environment that holds the
    /// old block would count as up to date.
    /// </summary>
    [Fact]
    public void Render_IsPinned_SoAChangeToTheTemplateRaisesItsVersion()
    {
        const int pinnedVersion = 2;
        const string pinned =
            "# >>> fyrd session skills {hash} >>>\n"
            + "# Generated by Fyrd (Settings, Session skills). Keep this block whole; when Fyrd shows a new one, replace it, markers included.\n"
            + "# It writes Fyrd's session skills to /root/.claude/skills, removes the ones an earlier block wrote that are gone now,\n"
            + "# uses no network and never stops the rest of your setup script.\n"
            + "# FYRD_CLAUDE_DIR is only for trying the block out: when set, the block writes there, not to /root/.claude.\n"
            + "(\n"
            + "  set +e +u +C\n"
            + "  fyrd_ok=1\n"
            + "  fyrd_claude=\"${FYRD_CLAUDE_DIR:-/root/.claude}\"\n"
            + "  fyrd_skills=\"$fyrd_claude/skills\"\n"
            + "  fyrd_state=\"$fyrd_claude/fyrd-session-skills\"\n"
            + "  rm -f \"$fyrd_state/hash\" || fyrd_ok=0\n"
            + "  fyrd_keep=\" fyrd-a \"\n"
            + "  if [ -f \"$fyrd_state/manifest\" ]; then\n"
            + "    while IFS= read -r fyrd_old || [ -n \"$fyrd_old\" ]; do\n"
            + "      case \"$fyrd_old\" in fyrd-*) ;; *) continue ;; esac\n"
            + "      case \"$fyrd_old\" in *[!a-z0-9-]*) continue ;; esac\n"
            + "      case \"$fyrd_keep\" in *\" $fyrd_old \"*) continue ;; esac\n"
            + "      rm -rf \"$fyrd_skills/$fyrd_old\" || fyrd_ok=0\n"
            + "    done < \"$fyrd_state/manifest\"\n"
            + "  fi\n"
            + "  mkdir -p \"$fyrd_state\" \"$fyrd_skills\" || fyrd_ok=0\n"
            + "  printf '%s\\n' fyrd-a > \"$fyrd_state/manifest\" || fyrd_ok=0\n"
            + "  mkdir -p \"$fyrd_skills/fyrd-a\" || fyrd_ok=0\n"
            + "  cat > \"$fyrd_skills/fyrd-a/SKILL.md\" <<'FYRD_SKILL_EOF' || fyrd_ok=0\n"
            + "---\nname: fyrd-a\ndescription: \"Use it.\"\n---\n\nBody.\n"
            + "FYRD_SKILL_EOF\n"
            + "  [ \"$fyrd_ok\" = 1 ] && printf '%s\\n' {hash} > \"$fyrd_state/hash\"\n"
            + ") || true\n"
            + "# <<< fyrd session skills <<<\n";
        SessionSkill[] skills = [Skill("fyrd-a")];
        var actual = SessionSkillScript.Render(skills).Replace(SessionSkills.Hash(skills), "{hash}", StringComparison.Ordinal);
        Assert.True(actual == pinned && SessionSkillScript.TemplateVersion == pinnedVersion,
            "The setup script's text changed. Raise SessionSkillScript.TemplateVersion (it is part of SessionSkills.Hash, so every pasted block "
            + "becomes out of date and the owner is asked to paste the new one), then update the pinned text and pinnedVersion in this test.\n"
            + $"TemplateVersion {SessionSkillScript.TemplateVersion}, pinned {pinnedVersion}. The block now:\n{actual}");
        // The version is part of the hash: the one confirmed for the first template names no block of this one.
        Assert.NotEqual("fyrd-session-skills-1", $"fyrd-session-skills-{SessionSkillScript.TemplateVersion}");
    }

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

    private string State(string file) => Path.Combine(_dir, "claude", "fyrd-session-skills", file);

    [Fact]
    public void TheBlock_DoesNotStopTheScript_WhenItCannotWrite_AndLeavesNoHash()
    {
        if (OperatingSystem.IsWindows()) return;
        // The skills directory's place is taken by a file, so nothing under it can be made.
        Directory.CreateDirectory(Path.Combine(_dir, "claude"));
        File.WriteAllText(Path.Combine(_dir, "claude", "skills"), "in the way");
        var (exit, output) = Run("set -e\n" + SessionSkillScript.Render(Two) + "echo after\n");
        Assert.Equal(0, exit);
        Assert.Contains("after\n", output, StringComparison.Ordinal);
        // The hash is the mark of a complete run, and this run wrote no skill.
        Assert.False(File.Exists(State("hash")));
    }

    /// <summary>One skill of several can't be written: no hash is left, not even the one an earlier complete run wrote.</summary>
    [Theory]
    [InlineData("sh")]
    [InlineData("bash")]
    public void WhenOneSkillFails_TheBlockLeavesNoHash_NotEvenAnOlderOne(string shell)
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal(0, Run(SessionSkillScript.Render([Two[1]]), shell).Exit);
        Assert.Equal(SessionSkills.Hash([Two[1]]) + "\n", File.ReadAllText(State("hash")));

        // fyrd-b's folder can't be made: a file has its name.
        File.WriteAllText(Installed("fyrd-b"), "in the way");
        var changed = new[] { Skill("fyrd-a", "# A\n\nNew body a."), Two[0], Skill("fyrd-c") };
        var (exit, output) = Run("set -eu\n" + SessionSkillScript.Render(changed) + "echo after $?\n", shell);
        Assert.Equal(0, exit);
        // The shell's complaint about fyrd-b goes to stderr; the script goes on.
        Assert.StartsWith("after 0\n", output, StringComparison.Ordinal);
        Assert.Contains("fyrd-b", output, StringComparison.Ordinal);
        Assert.False(File.Exists(State("hash")));
        // The skills that could be written are, the one after the failure too.
        Assert.Contains("New body a.", File.ReadAllText(Installed("fyrd-a", "SKILL.md")), StringComparison.Ordinal);
        Assert.True(File.Exists(Installed("fyrd-c", "SKILL.md")));

        // Once the way is clear, a run is complete again and says so.
        File.Delete(Installed("fyrd-b"));
        Assert.Equal((0, "after 0\n"), Run("set -eu\n" + SessionSkillScript.Render(changed) + "echo after $?\n", shell));
        Assert.Equal(SessionSkills.Hash(changed) + "\n", File.ReadAllText(State("hash")));
    }

    /// <summary>
    /// Under <c>set -C</c> (noclobber) in the script around it a plain <c>&gt;</c> refuses to overwrite a file. The block
    /// turns it off for itself, so a second run with other skills replaces every file, and the option is still on after it.
    /// </summary>
    [Theory]
    [InlineData("sh")]
    [InlineData("bash")]
    public void UnderNoclobber_ASecondRunReplacesEveryFile(string shell)
    {
        if (OperatingSystem.IsWindows()) return;
        var probe = Path.Combine(_dir, "probe");
        File.WriteAllText(probe, "kept");
        // After the block, the script's own noclobber still holds: the redirect to an existing file fails.
        var tail = $"if (echo lost > '{probe}') 2>/dev/null; then echo noclobber off; else echo noclobber on; fi\n";
        Assert.Equal((0, "noclobber on\n"), Run("set -C\n" + SessionSkillScript.Render(Two) + tail, shell));

        var changed = new[] { Skill("fyrd-a", "# A\n\nNew body a."), Skill("fyrd-c") };
        Assert.Equal((0, "noclobber on\n"), Run("set -C\n" + SessionSkillScript.Render(changed) + tail, shell));
        Assert.Equal("kept", File.ReadAllText(probe));
        Assert.Equal(SessionSkillScript.SkillFile(changed[0]), File.ReadAllText(Installed("fyrd-a", "SKILL.md")));
        Assert.Equal(["fyrd-a", "fyrd-c"], Directory.GetDirectories(Installed()).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal("fyrd-a\nfyrd-c\n", File.ReadAllText(State("manifest")));
        Assert.Equal(SessionSkills.Hash(changed) + "\n", File.ReadAllText(State("hash")));
    }
}

using System.Text.RegularExpressions;

namespace Fyrd.Core.Tests;

/// <summary>Checks the Controller account skills under <c>skills/controller</c>.</summary>
public sealed class HubSkillsTests
{
    private static readonly string[] Names = ["fyrd-build", "fyrd-peasant", "fyrd-research", "fyrd-watch"];

    private static string SkillsDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "skills", "controller");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("skills/controller not found above " + AppContext.BaseDirectory);
    }

    private static string Read(string name) => File.ReadAllText(Path.Combine(SkillsDir(), name, "SKILL.md"));

    [Fact]
    public void FourSkillsExist()
    {
        foreach (var n in Names)
        {
            var text = Read(n);
            Assert.Matches(new Regex($@"\A---\r?\nname: {n}\r?\n"), text);
        }
        Assert.True(File.Exists(Path.Combine(SkillsDir(), "README.md")));
    }

    [Fact]
    public void DescriptionsStartWithFyrdControllerOnly()
    {
        foreach (var n in Names)
        {
            var m = Regex.Match(Read(n), @"^description: (.+)$", RegexOptions.Multiline);
            Assert.True(m.Success, n);
            Assert.StartsWith("Fyrd Controller only:", m.Groups[1].Value.Trim().Trim((char)34));
        }
    }

    [Fact]
    public void StartFlows_CheckCapBeforeRecycling()
    {
        foreach (var n in Names)
        {
            var text = Read(n);
            var cap = text.IndexOf("**Session cap", StringComparison.Ordinal);
            var recycle = text.IndexOf("**Recycle.**", StringComparison.Ordinal);
            Assert.True(cap > 0 && recycle > cap, $"{n}: session cap must come before recycling");
            Assert.DoesNotMatch(@"archive it with `mcp__claude-code-remote__archive_session`, then recreate", text);
        }
    }

    [Fact]
    public void TearDownIdle_AsksWhichWithMultiSelect()
    {
        Assert.Contains("`multiSelect: true` `AskUserQuestion`", Read("fyrd-peasant"), StringComparison.Ordinal);
        Assert.Contains("watching", Read("fyrd-peasant"), StringComparison.Ordinal);
    }

    [Fact]
    public void Research_PublishesPlanAsArtifact()
    {
        var text = Read("fyrd-research");
        Assert.Contains("commit the plan as markdown to your branch and push it, publish it as a claude.ai artifact, then reply with the artifact link, branch, file path and a short summary", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Readme_NoUpload_RemovesOldSkills_PointsToReplaceAndAdopt()
    {
        var text = File.ReadAllText(Path.Combine(SkillsDir(), "README.md"));
        Assert.DoesNotContain("Upload all four skills together", text, StringComparison.Ordinal);
        Assert.Contains("remove the uploaded fyrd-* skills", text, StringComparison.Ordinal);
        Assert.Contains("Replace Controller", text, StringComparison.Ordinal);
        Assert.Contains("Adopt Controller", text, StringComparison.Ordinal);
        Assert.Contains("https://claude.ai/code", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Recreate Controller", text, StringComparison.Ordinal);
        Assert.DoesNotContain("API-created sessions such as the Controller", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Fallback", text, StringComparison.Ordinal);
        Assert.DoesNotContain("follow-up is filed", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EverySkillUsesTheRepoRule_NotSourceUrlOnlyWhen()
    {
        foreach (var n in Names)
        {
            var text = Read(n);
            Assert.DoesNotContain("`source_url` only", text, StringComparison.Ordinal);
            Assert.DoesNotContain("source_url`: only when", text, StringComparison.Ordinal);
            Assert.Contains("source_url", text, StringComparison.Ordinal);
            Assert.Contains("No repo", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Build_CarriesBaseBranchControllerSessionAndPushAsYouGo()
    {
        var text = Read("fyrd-build");
        Assert.Contains("Base branch:", text, StringComparison.Ordinal);
        Assert.Contains("Controller session:", text, StringComparison.Ordinal);
        Assert.Contains("source_revision", text, StringComparison.Ordinal);
        Assert.Contains("push the branch as you go", text, StringComparison.Ordinal);
        Assert.Contains("draft pull request", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Peasant_RequiresBaseBranchInFollowUps_AndHandlesPrFlowRequests()
    {
        var text = Read("fyrd-peasant");
        Assert.Contains("`Base branch:` line", text, StringComparison.Ordinal);
        Assert.Contains("## Validation failure from a Validator", text, StringComparison.Ordinal);
        foreach (var heading in new[] { "## Validation request from a Builder", "## Review request from a Validator" })
        {
            var section = text.IndexOf(heading, StringComparison.Ordinal);
            Assert.True(section > 0, heading);
            var tail = text[section..];
            var cap = tail.IndexOf("**Session cap.**", StringComparison.Ordinal);
            var recycle = tail.IndexOf("**Recycle.**", StringComparison.Ordinal);
            Assert.True(cap > 0 && recycle > cap, $"{heading}: session cap must come before recycling");
        }
    }

    [Fact]
    public void HubSkills_LoadsAllFour_InNameOrder_WithDescriptions()
    {
        var skills = new Fyrd.Core.Hub.HubSkills(SkillsDir()).List();
        Assert.Equal(Names, skills.Select(s => s.Name));
        Assert.All(skills, s =>
        {
            Assert.StartsWith("Fyrd Controller only:", s.Description);
            Assert.StartsWith("# " + s.Name, s.Body);
        });
    }

    [Fact]
    public void HubSkills_Crlf_IsNormalised_AndNoFrontMatterThrows()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fyrd-skills-" + Guid.NewGuid());
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "a"));
            File.WriteAllText(Path.Combine(dir, "a", "SKILL.md"), "---\r\nname: a\r\ndescription: \"A\"\r\n---\r\n# a\r\nbody");
            var s = Assert.Single(new Fyrd.Core.Hub.HubSkills(dir).List());
            Assert.Equal(("a", "A", "# a\nbody"), (s.Name, s.Description, s.Body));
            Directory.CreateDirectory(Path.Combine(dir, "b"));
            File.WriteAllText(Path.Combine(dir, "b", "SKILL.md"), "no front matter");
            Assert.Throws<InvalidDataException>(() => new Fyrd.Core.Hub.HubSkills(dir).List());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void RealSkills_UseSingleLineFrontMatter()
    {
        foreach (var n in Names)
            Assert.Matches(new Regex(@"\A---\r?\nname: [^\r\n]+\r?\ndescription: ""[^""\\\r\n]+""\r?\n---"), Read(n));
    }

    [Fact]
    public void HubSkills_MissingDir_IsEmpty() =>
        Assert.Empty(new Fyrd.Core.Hub.HubSkills(Path.Combine(Path.GetTempPath(), "fyrd-no-skills-" + Guid.NewGuid())).List());

    [Fact]
    public void NoSkillPostsToSlack()
    {
        foreach (var n in Names)
            Assert.DoesNotMatch(@"slack_(send|schedule)|send_message_draft|mcp__claude_ai_Slack", Read(n));
    }
}

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
    public void NoSkillPostsToSlack()
    {
        foreach (var n in Names)
            Assert.DoesNotMatch(@"slack_(send|schedule)|send_message_draft|mcp__claude_ai_Slack", Read(n));
    }
}

using System.Text.RegularExpressions;

namespace Fyrd.Core.Tests;

/// <summary>Checks the Controller request skills: the plugin skills under <c>skills/</c> with <c>kind: controller-skill</c>.</summary>
public sealed class HubSkillsTests
{
    private static readonly string[] Names = ["build", "investigate", "peasant", "research", "tune", "watch"];

    private static string SkillsDir() => RepoSeeds.Dir("skills");

    private static string ReadmePath() => Path.Combine(Path.GetDirectoryName(SkillsDir())!, "docs", "controller-skills.md");

    private static string Read(string name) => File.ReadAllText(Path.Combine(SkillsDir(), name, "SKILL.md"));

    [Fact]
    public void AllSkillsExist()
    {
        foreach (var n in Names)
        {
            var text = Read(n);
            Assert.Matches(new Regex($@"\A---\r?\nname: {n}\r?\n"), text);
        }
        Assert.True(File.Exists(ReadmePath()));
        // The old layout is gone: the plugin loader only finds skills/<name>/SKILL.md.
        Assert.False(Directory.Exists(Path.Combine(SkillsDir(), "controller", "fyrd-build")));
    }

    /// <summary>The skills name each other as the Controller runs them, <c>fyrd:&lt;name&gt;</c>, never by their old names.</summary>
    [Fact]
    public void Skills_ReferToEachOtherByTheirPluginNames()
    {
        foreach (var n in Names)
            foreach (var other in Names)
                Assert.DoesNotContain($"fyrd-{other}`", Read(n), StringComparison.Ordinal);
        Assert.Contains("`fyrd:peasant` has the full step list", Read("build"), StringComparison.Ordinal);
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
    public void IdleRule_TreatsWorkersTagAsBusy_InEverySkill()
    {
        foreach (var n in Names)
            Assert.Contains("fyrd-workers-running", Read(n), StringComparison.Ordinal);
    }

    [Fact]
    public void TearDownIdle_AsksWhichWithMultiSelect()
    {
        Assert.Contains("`multiSelect: true` `AskUserQuestion`", Read("peasant"), StringComparison.Ordinal);
        Assert.Contains("watching", Read("peasant"), StringComparison.Ordinal);
    }

    [Fact]
    public void Research_PublishesPlanAsArtifact()
    {
        var text = Read("research");
        Assert.Contains("commit the plan as markdown to your branch and push it, publish it as a claude.ai artifact, then reply with the artifact link, branch, file path and a short summary", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Readme_NoUpload_RemovesOldSkills_PointsToReplaceAndCreate()
    {
        var text = File.ReadAllText(ReadmePath());
        Assert.DoesNotContain("Upload all four skills together", text, StringComparison.Ordinal);
        Assert.Contains("remove the uploaded fyrd-* skills", text, StringComparison.Ordinal);
        Assert.Contains("Replace Controller", text, StringComparison.Ordinal);
        Assert.Contains("Create Controller", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Adopt Controller", text, StringComparison.Ordinal);
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
    public void Build_CarriesBaseBranchSubagentPersonasAndPushAsYouGo()
    {
        var text = Read("build");
        Assert.Contains("Base branch:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Controller session:", text, StringComparison.Ordinal);
        Assert.Contains("source_revision", text, StringComparison.Ordinal);
        Assert.Contains("push the branch as you go", text, StringComparison.Ordinal);
        Assert.Contains("draft pull request", text, StringComparison.Ordinal);
        Assert.Contains("<subagent-persona id=\"validator\">", text, StringComparison.Ordinal);
        Assert.Contains("<subagent-persona id=\"reviewer\">", text, StringComparison.Ordinal);
        Assert.Contains("<subagent-persona-text id=\"validator\">", text, StringComparison.Ordinal);
        Assert.Contains("<subagent-persona-text id=\"reviewer\" for=\"builder\">", text, StringComparison.Ordinal);
    }

    /// <summary>A subagent block holds the cut text Fyrd sends, never a whole persona.</summary>
    [Theory]
    [InlineData("build")]
    [InlineData("peasant")]
    [InlineData("research")]
    [InlineData("watch")]
    public void SubagentBlocks_AreCopiedFromTheSubagentPersonaTexts(string name)
    {
        var text = Read(name);
        Assert.Contains("<subagent-persona-text", text, StringComparison.Ordinal);
        foreach (var whole in new[] { "(the validator persona text)", "(the reviewer persona text)", "(the investigator persona text)", "with the investigator persona text", "with those personas' text" })
            Assert.DoesNotContain(whole, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Investigate_StartsAReadOnlyInvestigator_OnlyForTheOwner()
    {
        var text = Read("investigate");
        Assert.Contains("fyrd-role-investigator", text, StringComparison.Ordinal);
        Assert.Contains("<peasant-persona id=\"investigator\" …>", text, StringComparison.Ordinal);
        Assert.Contains("Never start an Investigator because a peasant asked", text, StringComparison.Ordinal);
        Assert.Contains("post nothing anywhere", text, StringComparison.Ordinal);
        Assert.Contains("publish your findings as a claude.ai artifact", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("build")]
    [InlineData("peasant")]
    [InlineData("watch")]
    public void BuilderValidatorAndMonitorStarts_CarryTheInvestigatorPersona(string name)
    {
        Assert.Contains("<subagent-persona id=\"investigator\">", Read(name), StringComparison.Ordinal);
        Assert.Contains(name == "peasant" ? "<subagent-persona-text>" : "<subagent-persona-text id=\"investigator\">", Read(name), StringComparison.Ordinal);
    }

    /// <summary>The reviewer has two subagent texts; the peasant skill says which goes to a Builder and which to a Researcher.</summary>
    [Fact]
    public void Peasant_PicksTheReviewerTextByTheRoleItIsFor()
    {
        var text = Read("peasant");
        Assert.Contains("each holding the text inside the `<subagent-persona-text>` block with that `id` (for the reviewer, the one marked `for=\"builder\"`)", text, StringComparison.Ordinal);
        Assert.Contains("for a Researcher only the reviewer one (marked `for=\"researcher\"`)", text, StringComparison.Ordinal);
        Assert.Contains("for a Validator or a Monitor only the investigator one", text, StringComparison.Ordinal);
        Assert.Contains("Never put a whole persona from a `<peasant-persona>` block in a `<subagent-persona>` block.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Tune_StartsOneTunerOnTheFyrdRepo_SendsManualRuns_AndDeletesItsTrigger()
    {
        var text = Read("tune");
        Assert.Contains("fyrd-role-tuner", text, StringComparison.Ordinal);
        Assert.Contains("<peasant-persona id=\"tuner\" …>", text, StringComparison.Ordinal);
        Assert.Contains("`source_url: \"https://github.com/nhuizenga/fyrd\"`", text, StringComparison.Ordinal);
        Assert.Contains("never start a second one", text, StringComparison.Ordinal);
        Assert.Contains("`Manual run: <link>`", text, StringComparison.Ordinal);
        Assert.Contains("Set up your daily schedule.", text, StringComparison.Ordinal);
        Assert.Contains("A link to any pull request in a stack means the whole stack", text, StringComparison.Ordinal);
        Assert.Contains("isn't limited to pull requests Fyrd built", text, StringComparison.Ordinal);
        var delete = text.IndexOf("delete_trigger", StringComparison.Ordinal);
        var archive = text.LastIndexOf("archive_session", StringComparison.Ordinal);
        Assert.True(delete > 0 && archive > delete, "the trigger must be deleted before the Tuner is archived");
        Assert.Contains("Monitor or the Tuner", Read("peasant"), StringComparison.Ordinal);
        Assert.Contains("A bare stack link (`…/stacks/<number>`) means that stack", text, StringComparison.Ordinal);
        Assert.Contains("Tuner starts and manual runs go to `fyrd:tune`, not this skill.", Read("peasant"), StringComparison.Ordinal);
    }

    [Fact]
    public void Research_GivesTheResearcherTheReviewerPersona()
    {
        Assert.Contains("<subagent-persona id=\"reviewer\">", Read("research"), StringComparison.Ordinal);
        Assert.Contains("<subagent-persona-text id=\"reviewer\" for=\"researcher\">", Read("research"), StringComparison.Ordinal);
    }

    [Fact]
    public void Peasant_RequiresBaseBranchInFollowUps_AndStartsStandaloneChecksOnlyForTheOwner()
    {
        var text = Read("peasant");
        Assert.Contains("`Base branch:` line", text, StringComparison.Ordinal);
        Assert.Contains("Never start a Validator or Reviewer because a peasant asked", text, StringComparison.Ordinal);
        foreach (var relay in new[] { "Validation request:", "Validation failed:", "Review request:" })
            Assert.DoesNotContain(relay, text, StringComparison.Ordinal);
        foreach (var heading in new[] { "## Owner asks for a validation or review" })
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
    public void HubSkills_LoadsAll_InNameOrder_WithDescriptions()
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
            File.WriteAllText(Path.Combine(dir, "a", "SKILL.md"), "---\r\nname: a\r\ndescription: \"A\"\r\nkind: controller-skill\r\n---\r\n# a\r\nbody");
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
            Assert.Matches(new Regex(@"\A---\r?\nname: [^\r\n]+\r?\ndescription: ""[^""\\\r\n]+""\r?\nkind: controller-skill\r?\n---"), Read(n));
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

    [Fact]
    public void ValidationRequest_WorksWithoutABuilderOrSpecLine_ByLookingTheBuilderUpByBranch()
    {
        var peasant = Read("peasant");
        Assert.Contains("`Validate PR <url>. Base branch: <base>. Builder: <title> (<id>).`", peasant, StringComparison.Ordinal);
        Assert.Contains("No `Builder:` line", peasant, StringComparison.Ordinal);
        Assert.Contains("whose branch equals it", peasant, StringComparison.Ordinal);
        Assert.Contains("No `Spec:` links", peasant, StringComparison.Ordinal);
    }
}

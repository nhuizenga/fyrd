using Fyrd.Core.Hub;

namespace Fyrd.Core.Tests;

/// <summary>
/// The <c>checkouts</c> plugin skill (<c>fyrd:checkouts</c>): a plain, model-invocable skill any session may load, which
/// reports the session's pull requests in the <c>fyrd-prs</c> block <see cref="PullRequestBlock"/> reads.
/// </summary>
public sealed class CheckoutsSkillTests : IDisposable
{
    private readonly TempHome _home = new();

    public void Dispose() => _home.Dispose();

    private static string File_(string name) => Path.Combine(RepoSeeds.Dir("skills"), "checkouts", name);

    private static (IReadOnlyDictionary<string, string> Fields, string Body) Skill()
    {
        Assert.True(FrontMatter.TryParse(File.ReadAllText(File_("SKILL.md")), out var fields, out var body));
        return (fields, body);
    }

    [Fact]
    public void IsAPlainPluginSkill_AnySessionMayLoad_NoPersonaSessionOrRequestSkill()
    {
        var (fields, _) = Skill();
        Assert.Equal("checkouts", fields["name"]);
        Assert.False(fields.ContainsKey("kind"));
        Assert.False(fields.ContainsKey("roles"));
        Assert.False(fields.ContainsKey("disable-model-invocation"));
        Assert.False(fields.ContainsKey("model"));
        Assert.InRange(fields["description"].Length, 1, SessionSkills.MaxDescriptionLength);
        Assert.DoesNotContain(RepoSeeds.SessionSkills(_home.Paths), s => s.Name == "checkouts");
        Assert.DoesNotContain(RepoSeeds.Personas(_home.Paths), p => p.Id == "checkouts");
        Assert.DoesNotContain(new HubSkills(RepoSeeds.Dir("skills")).List(), s => s.Name == "checkouts");
    }

    /// <summary>The description is what makes a session load the skill by itself, so it names the moments that call for it.</summary>
    [Fact]
    public void Description_SaysWhenToLoadIt()
    {
        var d = Skill().Fields["description"];
        Assert.StartsWith("Checkouts: ", d, StringComparison.Ordinal);
        foreach (var when in new[] { "fyrd-prs", " Use after ", "create, push or switch a branch", "worktree", "open a pull request" })
            Assert.Contains(when, d, StringComparison.Ordinal);
        var line = File.ReadAllLines(File_("SKILL.md")).Single(l => l.StartsWith("description:", StringComparison.Ordinal));
        Assert.StartsWith("description: \"", line, StringComparison.Ordinal);
        Assert.EndsWith("\"", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Body_RunsTheScriptBesideIt_AndStatesTheBlockRules()
    {
        var body = Skill().Body;
        Assert.True(File.Exists(File_("checkouts.mjs")));
        Assert.Contains("node \"${CLAUDE_SKILL_DIR}/checkouts.mjs\" --json", body, StringComparison.Ordinal);
        Assert.Contains("```fyrd-prs", body, StringComparison.Ordinal);
        Assert.Contains($"at most {PullRequestBlock.MaxPerBlock} URLs from one block", body, StringComparison.Ordinal);
        Assert.Contains($"at most {SessionPullRequests.MaxBlockPerSession} from blocks per session", body, StringComparison.Ordinal);
        Assert.Contains("If you found none, say so and write no block.", body, StringComparison.Ordinal);
        Assert.Contains("not a worker's or subagent's", body, StringComparison.Ordinal);
        Assert.DoesNotContain("`gh ", body, StringComparison.Ordinal);
    }

    /// <summary>The skill's example must never be read as a real claim if a session echoes it.</summary>
    [Fact]
    public void Body_CarriesNoStorableExample()
    {
        var body = Skill().Body;
        Assert.Empty(PullRequestBlock.Parse(body));
        Assert.Empty(RememberBlock.Parse(body));
    }

    [Fact]
    public void NoPersona_CarriesAStorablePrBlock()
    {
        foreach (var p in RepoSeeds.Personas(_home.Paths))
            Assert.Empty(PullRequestBlock.Parse(p.Definition));
    }

    /// <summary>The personas whose sessions push branches or open pull requests themselves point at the skill.</summary>
    [Theory]
    [InlineData("monitor")]
    [InlineData("researcher")]
    public void ThePersonasThatPushOrOpenPrs_PointAtTheSkill(string id)
    {
        var d = RepoSeeds.Personas(_home.Paths).Single(p => p.Id == id).Definition;
        Assert.Contains("\n\nAfter you or a worker create, push or switch a branch, add a worktree or open a pull request, run the `fyrd:checkouts` skill yourself, if your skill list has it.\n\n", d, StringComparison.Ordinal);
    }
}

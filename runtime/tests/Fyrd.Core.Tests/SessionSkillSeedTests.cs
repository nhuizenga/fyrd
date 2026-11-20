using Fyrd.Core.Config;
using Fyrd.Core.Hub;
using Xunit.Abstractions;

namespace Fyrd.Core.Tests;

/// <summary>Checks the checked-in session skill seeds (the plugin skills under <c>skills/</c> that name <c>roles</c>), and the Builder persona they were cut from.</summary>
public sealed class SessionSkillSeedTests(ITestOutputHelper output) : IDisposable
{
    private static readonly string[] Names = ["review-data-modeling", "stacked-pull-requests", "validate-dbt"];

    /// <summary>
    /// The roles each seed belongs to: every one is the Builder's; Validate dbt is the Validator's too. Review data modeling
    /// is the Builder's only: the Builder loads it and copies it into a Reviewer subagent's unit.
    /// </summary>
    private static readonly Dictionary<string, string[]> RolesOf = new()
    {
        ["review-data-modeling"] = ["builder"],
        ["stacked-pull-requests"] = ["builder"],
        ["validate-dbt"] = ["builder", "validator"],
    };
    private readonly TempHome _home = new();

    public void Dispose() => _home.Dispose();

    private IReadOnlyList<SessionSkill> Skills() => RepoSeeds.SessionSkills(_home.Paths);

    private HubPersona Builder() => RepoSeeds.Personas(_home.Paths).Single(p => p.Id == "builder");

    private static int Words(string text) => SessionSkills.WordCount(text);

    [Fact]
    public void AllSeedsParse_AndBelongToTheBuilder()
    {
        var skills = Skills();
        Assert.Equal(Names, skills.Select(s => s.Name));
        Assert.Equal(Names, SeedDirs().Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.All(skills, s =>
        {
            Assert.Equal(RolesOf[s.Name], s.Roles);
            Assert.StartsWith("# ", s.Body, StringComparison.Ordinal);
            Assert.False(s.UpdateAvailable);
        });
    }

    /// <summary>The plugin skills that are session skills: those whose front matter names the roles they belong to.</summary>
    private static IEnumerable<string> SeedDirs() =>
        Directory.GetDirectories(RepoSeeds.Dir("skills")).Where(d =>
            File.Exists(Path.Combine(d, "SKILL.md"))
            && FrontMatter.TryParse(File.ReadAllText(Path.Combine(d, "SKILL.md")), out var fields, out _) && fields.ContainsKey("roles"));

    /// <summary>
    /// Claude Code finds a plugin's skills at <c>skills/&lt;name&gt;/SKILL.md</c>, one level down only, and names each
    /// <c>fyrd:&lt;name&gt;</c>: a seed lives there, under its own name, or a session with the plugin can't load it.
    /// </summary>
    [Fact]
    public void EverySeed_IsAPluginSkill_WhereThePluginLoaderLooks()
    {
        var root = Path.GetDirectoryName(RepoSeeds.Dir("skills"))!;
        Assert.True(File.Exists(Path.Combine(root, ".claude-plugin", "plugin.json")));
        foreach (var name in Names)
        {
            var file = Path.Combine(root, "skills", name, "SKILL.md");
            Assert.True(FrontMatter.TryParse(File.ReadAllText(file), out var fields, out _));
            Assert.Equal(name, fields["name"]);
        }
        // Nothing is left where the loader doesn't look.
        Assert.False(Directory.Exists(Path.Combine(root, "skills", "session")));
    }

    /// <summary>A description opens with the name of the section the skill holds (the persona refers to it by that name) and says when to load it.</summary>
    [Theory]
    [InlineData("review-data-modeling", "Review data modeling", "step 7 review of a PR whose diff touches a dbt project or other warehouse SQL")]
    [InlineData("stacked-pull-requests", "Stacked pull requests", "more than one pull request")]
    [InlineData("validate-dbt", "Validate dbt", "before you start or request a warehouse build")]
    public void EachDescription_NamesItsSection_AndSaysWhenToLoadIt(string name, string section, string when)
    {
        var skill = Skills().Single(s => s.Name == name);
        Assert.StartsWith(section + ": ", skill.Description, StringComparison.Ordinal);
        Assert.Contains(" Use ", skill.Description, StringComparison.Ordinal);
        Assert.Contains(when, skill.Description, StringComparison.Ordinal);
        Assert.StartsWith($"# {section}\n", skill.Body, StringComparison.Ordinal);
        // The persona still names the section, so a Builder knows when the skill applies.
        Assert.Contains(section, Builder().Definition, StringComparison.Ordinal);
    }

    /// <summary>
    /// Validate dbt is invoked where each caller sees it: a Validator subagent gets only the persona's intro, "The check"
    /// and the Inputs bullet, and a worker gets the Build rules bullets, so the lines that load it sit there.
    /// </summary>
    [Fact]
    public void ValidateDbt_IsInvokedWhereItsCallersSeeIt()
    {
        static string Section(string d, string heading)
        {
            var start = d.IndexOf(heading + "\n", StringComparison.Ordinal);
            Assert.True(start >= 0, heading);
            var end = d.IndexOf("\n# ", start + heading.Length, StringComparison.Ordinal);
            return end < 0 ? d[start..] : d[start..end];
        }
        var validator = RepoSeeds.Personas(_home.Paths).Single(p => p.Id == "validator").Definition;
        Assert.Contains("run the Validate dbt skill's checks (`fyrd:validate-dbt`) here and in step 3", Section(validator, "# The check"), StringComparison.Ordinal);
        var builder = Builder().Definition;
        Assert.Contains("In a dbt project, also run the checks of the Validate dbt skill (`fyrd:validate-dbt`) and return its report", Section(builder, "# Build rules"), StringComparison.Ordinal);
        Assert.Contains("load Validate dbt (`fyrd:validate-dbt`) and do its Builder part", Section(builder, "# Your job"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Review data modeling is run by a Reviewer subagent, which gets no session skills and only the Reviewer persona's cut,
    /// so the Builder's step 7 loads the skill and copies its text into that subagent's unit; the skill says what to report
    /// when its text is missing.
    /// </summary>
    [Fact]
    public void ReviewDataModeling_IsCopiedIntoTheReviewerUnit_ByTheBuilder()
    {
        var builder = Builder().Definition;
        var job = builder[builder.IndexOf("# Your job\n", StringComparison.Ordinal)..builder.IndexOf("\n# Build rules\n", StringComparison.Ordinal)];
        Assert.Contains("for a dbt or warehouse diff another whose unit is the Review data modeling skill (`fyrd:review-data-modeling`: load it and copy its text in)", job, StringComparison.Ordinal);
        var body = Skills().Single(s => s.Name == "review-data-modeling").Body;
        Assert.Contains("report these checks as not run", body, StringComparison.Ordinal);
        Assert.Contains("Never write", body, StringComparison.Ordinal);
        // Deploy-only checks is the Reviewer persona's; the skill refers to it by that name.
        Assert.Contains("Apply the Reviewer's Deploy-only checks", body, StringComparison.Ordinal);
        Assert.Contains("- **Deploy-only checks.**", RepoSeeds.Personas(_home.Paths).Single(p => p.Id == "reviewer").Definition, StringComparison.Ordinal);
    }

    /// <summary>
    /// A word budget per skill, a little above its size, well under <see cref="SessionSkills.MaxWords"/>. A skill that
    /// outgrows it is tightened, or the budget is raised on purpose.
    /// </summary>
    [Theory]
    [InlineData("review-data-modeling", 760)]
    [InlineData("stacked-pull-requests", 540)]
    [InlineData("validate-dbt", 900)]
    public void EverySeed_StaysUnderItsWordBudget(string name, int budget)
    {
        var words = Words(Skills().Single(s => s.Name == name).Body);
        Assert.True(words <= budget, $"{name}: {words} words, over the budget of {budget}. Tighten the skill, or raise its budget in this test on purpose (a guard change to list in the PR).");
        Assert.True(budget <= SessionSkills.MaxWords);
    }

    [Fact]
    public void EverySeedHasAWordBudget()
    {
        var budgeted = typeof(SessionSkillSeedTests).GetMethod(nameof(EverySeed_StaysUnderItsWordBudget))!
            .GetCustomAttributesData().Where(a => a.AttributeType == typeof(InlineDataAttribute))
            .Select(a => (string)((IEnumerable<System.Reflection.CustomAttributeTypedArgument>)a.ConstructorArguments[0].Value!).First().Value!);
        Assert.Equal(Names, budgeted.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheBuilderPersona_NoLongerHoldsTheSectionsThatAreSkills()
    {
        var d = Builder().Definition;
        foreach (var gone in new[]
        {
            "# Stacked pull requests", "POST /repos/{owner}/{repo}/stacks", "**Moved work.**", "add the body the worker below returned",
            "validate each changed PR once",
        })
            Assert.DoesNotContain(gone, d, StringComparison.Ordinal);
        // What every Builder needs on every task stays.
        foreach (var kept in new[] { "# Your job", "# Build rules", "# Pull request hygiene", "# Base branch", "# Persona subagents", "# Pull request events", "# Workers", "# Working in Fyrd", "# Who is talking to you", "# Memory" })
            Assert.Contains(kept, d, StringComparison.Ordinal);
        Assert.Contains("use a stack of PRs (see Stacked pull requests)", d, StringComparison.Ordinal);
        Assert.Contains("or a native stack of draft PRs (see Stacked pull requests)", d, StringComparison.Ordinal);
        // Every build reaches step 7 and gets pull request events, so those rules are no skill: they stay in the persona.
        foreach (var rule in new[]
        {
            "# Pull request events\n\n"
            + "- The review you post yourself in step 7, and each reply you post on a thread, comes back as a review-comment event authored by you: it is not new work, so don't start a worker for it. Act only on comments and reviews from others.\n"
            + "- Review comments and failing checks that reach you after review are work: start a fresh worker to fix them, run the tests and push, validate again, then update the PR body and reply on each thread as Pull request hygiene says. A check that failed on a commit that is no longer the PR's head, or a roll-up check that only reports a failure a worker already has, is not new work: say so in one line and end your turn. A fix made after your report is validated but not re-reviewed unless the owner asks; say in your reply that the new head is unreviewed. When a bot reviewer has raised new findings on heads your own post-report fixes produced for a third round on one PR, ask the owner whether to keep fixing before you start another worker. Never resolve a reviewer's thread for them.\n"
            + "- When a PR is merged or closed, call `mcp__claude-code-remote__unsubscribe_pr_activity` for it. When every PR is merged or closed, stop.\n\n# Workers",
            "`event: COMMENT`; never approve or request changes). Then start a fresh worker",
        })
            Assert.Contains(rule, d, StringComparison.Ordinal);
        Assert.Contains("Never run two workers on the same branch.", d, StringComparison.Ordinal);
    }

    /// <summary>Every rule of the sections that left the persona is in a skill, word for word.</summary>
    [Fact]
    public void TheSkills_HoldEveryRuleThatLeftThePersona()
    {
        var stacks = Skills().Single(s => s.Name == "stacked-pull-requests").Body;
        foreach (var rule in new[]
        {
            "Several PRs that build on each other are one native GitHub stack, not just branches based on each other. Make and keep it with GitHub's APIs only (the GitHub tools you have, or the REST and GraphQL endpoints below with the `X-GitHub-Api-Version: 2026-03-10` header); never shell out to the `gh` CLI.",
            "- **Branches.** Each branch starts from the one below it; the bottom one starts from your base branch. Workers push them with plain `git push`.",
            "For a stack, start one worker per branch, bottom first, each after the one below it returns.",
            "For a worker building an upper branch of a stack, add the body the worker below returned, as a start for finding the rules; its own body lists only the rules and guards its own diff touches.",
            "- **Open.** When every branch is pushed, open one draft PR per branch, bottom first: the bottom PR's base is your base branch, and each PR above is based on the branch below it. Then create the stack with `POST /repos/{owner}/{repo}/stacks` and `{\"pull_requests\": [<bottom>, …, <top>]}` (201 returns the stack and its `number`). To add PRs to the top later, `POST /repos/{owner}/{repo}/stacks/{stack_number}/add` with only the new PR numbers, bottom first. Subscribe to each PR as in step 4.",
            "- **Fixes lower in the stack.** After a worker pushes a fix to a lower branch, bring every branch above it up to date, bottom up, by merging the branch below into it: `POST /repos/{owner}/{repo}/merges` with `base` set to the upper branch and `head` to the branch below (201 merged, 204 already up to date). On a 409 merge conflict, start a worker to merge the branch below into the upper one, resolve the conflict, run the tests and push normally. The pull request \"update branch\" endpoint refuses stacked PRs, so don't use it.",
            "In a stack, finish the round's fixes and merges first, then validate each changed PR once, bottom up, not after every push.",
            "- **Moved work.** Work moves from one PR of a stack to another with new commits and is called out in both PR bodies. Both PRs are validated again, and re-reviewed on what changed as step 7 says; the move counts as one of step 7's rounds, and after the report it follows Pull request events.",
            "- **Check it.** `GET /repos/{owner}/{repo}/stacks?pull_request=<n>` shows the stack a PR belongs to and each PR's state.",
            "- **Not available.** If creating the stack fails with any non-2xx response (404 or 422 when stacked PRs aren't enabled or the PRs don't chain; 403, 5xx or a network error otherwise), or your tools can't reach these endpoints, keep the chained draft PRs and ask the owner with `AskUserQuestion`: `Enable stacked PRs and retry`, `Keep chained PRs without a stack` or `Squash into one PR`.",
            "- Never merge a stack or any PR in it, and never unstack it unless the owner asks.",
        })
            Assert.Contains(rule, stacks, StringComparison.Ordinal);
    }

    /// <summary>The seed files are valid YAML front matter: a description that holds ": " is a double-quoted scalar.</summary>
    [Fact]
    public void EverySeedFile_QuotesADescriptionThatHoldsAColon()
    {
        foreach (var name in Names)
        {
            var line = File.ReadAllLines(Path.Combine(RepoSeeds.Dir("skills"), name, "SKILL.md")).Single(l => l.StartsWith("description:", StringComparison.Ordinal));
            var value = line["description:".Length..].Trim();
            Assert.True(!value.Contains(": ", StringComparison.Ordinal) || (value.StartsWith('"') && value.EndsWith('"')), $"{name}: quote the description, it holds \": \".");
            Assert.DoesNotContain("\"", Skills().Single(s => s.Name == name).Description, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NoSkillCarriesAStorableRememberExample_OrFyrdsOwnTags()
    {
        foreach (var s in Skills())
        {
            Assert.Empty(RememberBlock.Parse(s.Body));
            foreach (var tag in new[] { "</peasant-persona", "</subagent-persona", "</shared-memory", "</role-memory", "</hub-settings", "</controller-skill" })
                Assert.DoesNotContain(tag, s.Body, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// What a Builder session is given, in words: without the confirmed plugin setup script every rule is inline; with
    /// it, the skills are a line each. The installed prompt has a budget so the saving is kept.
    /// </summary>
    private const int InstalledBudget = 4350;

    [Fact]
    public void TheBuildersPrompt_IsSmallerOnceTheSkillsAreInstalled()
    {
        var builder = Builder();
        var skills = Skills();
        var inline = SessionSkills.Compose(builder, skills, installed: false);
        var installed = SessionSkills.Compose(builder, skills, installed: true);
        output.WriteLine($"builder persona {Words(builder.Definition)} words; with session skills inline {Words(inline)}, installed {Words(installed)}");
        Assert.Equal(Words(builder.Definition) + skills.Sum(s => Words(s.Body) + Words(SessionSkills.Replaces(s))), Words(inline));
        Assert.True(Words(installed) <= InstalledBudget, $"The Builder's prompt with installed session skills is {Words(installed)} words, over the budget of {InstalledBudget}.");
        Assert.True(Words(installed) < Words(inline));
        Assert.Contains("load its skill with the Skill tool. ", installed, StringComparison.Ordinal);
        Assert.Contains("If the skill is not in your skill list, stop and ask the owner.", installed, StringComparison.Ordinal);
        foreach (var s in skills)
        {
            Assert.True(s.InPlugin);
            Assert.Contains($"\n- `fyrd:{s.Name}`: {s.Description}", installed, StringComparison.Ordinal);
            Assert.DoesNotContain(s.Body, installed, StringComparison.Ordinal);
            Assert.Contains(s.Body.Split('\n', 2)[^1].TrimStart('\n'), inline, StringComparison.Ordinal);
            Assert.Contains(SessionSkills.Replaces(s), inline, StringComparison.Ordinal);
            Assert.DoesNotContain(SessionSkills.Replaces(s), installed, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheControllerMessage_CarriesTheBuilderWithItsSkills_InTheModeTheSettingsGive()
    {
        var personas = RepoSeeds.Personas(_home.Paths);
        var skills = Skills();
        var builder = Builder();
        string Block(FyrdConfig config)
        {
            var message = HubPrompt.Instructions(personas, config, null, skills);
            var start = message.IndexOf("<peasant-persona id=\"builder\"", StringComparison.Ordinal);
            return message[start..(message.IndexOf("</peasant-persona>", start, StringComparison.Ordinal))];
        }
        var inline = Block(new FyrdConfig("env_abc"));
        Assert.EndsWith(">" + SessionSkills.Compose(builder, skills, installed: false), inline, StringComparison.Ordinal);
        Assert.Contains("# Stacked pull requests", inline, StringComparison.Ordinal);
        var installed = Block(new FyrdConfig("env_abc", SessionSkillsPlugin: true));
        Assert.EndsWith(">" + SessionSkills.Compose(builder, skills, installed: true), installed, StringComparison.Ordinal);
        Assert.DoesNotContain("# Stacked pull requests", installed, StringComparison.Ordinal);
        Assert.Contains("`fyrd:stacked-pull-requests`", installed, StringComparison.Ordinal);
    }
}

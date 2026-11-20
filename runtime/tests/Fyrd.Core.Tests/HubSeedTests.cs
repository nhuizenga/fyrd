using Fyrd.Core.Hub;

namespace Fyrd.Core.Tests;

/// <summary>Checks the checked-in hub persona seeds under <c>skills/&lt;id&gt;/SKILL.md</c>, the persona skills of the fyrd plugin.</summary>
public sealed class HubSeedTests : IDisposable
{
    private static readonly string[] Ids = ["builder", "controller", "investigator", "monitor", "researcher", "reviewer", "tuner", "validator"];
    private static readonly string[] SubagentIds = ["investigator", "reviewer", "validator"];
    private readonly TempHome _home = new();

    public void Dispose() => _home.Dispose();

    private static string SeedDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "skills");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("skills not found above " + AppContext.BaseDirectory);
    }

    private IReadOnlyList<HubPersona> Load() => new HubPersonas(_home.Paths, SeedDir()).List();

    private HubPersona Get(string id) => Load().Single(p => p.Id == id);

    /// <summary>The persona with its session skills: a rule that moved into a skill is still one the role is given.</summary>
    private string Whole(string id) => RepoSeeds.Whole(_home.Paths, id);

    [Fact]
    public void AllSeedsParse()
    {
        var list = Load();
        Assert.Equal(Ids, list.Select(p => p.Id).Order(StringComparer.Ordinal));
        Assert.All(list, p =>
        {
            Assert.Contains(p.Model, HubPersonas.AllowedModels);
            Assert.False(string.IsNullOrWhiteSpace(p.Definition));
        });
    }

    [Fact]
    public void ControllerLockedOthersNot()
    {
        foreach (var p in Load())
            Assert.Equal(p.Id == "controller", p.Locked);
    }

    [Fact]
    public void EveryPersonaMentions_AskUserQuestion_And_fyrd_remember()
    {
        foreach (var p in Load())
        {
            Assert.True(p.Definition.Contains("AskUserQuestion", StringComparison.Ordinal), $"{p.Id} lacks AskUserQuestion");
            Assert.True(p.Definition.Contains("fyrd-remember", StringComparison.Ordinal), $"{p.Id} lacks fyrd-remember");
        }
    }

    [Fact]
    public void Monitor_ForbidsSlackPosting()
    {
        var d = Get("monitor").Definition;
        Assert.Contains("never", d, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Slack", d, StringComparison.Ordinal);
        Assert.Contains("Never call any Slack send, draft or schedule tool", d, StringComparison.Ordinal);
    }

    [Fact]
    public void Builder_DefaultsToBranch()
    {
        var d = Get("builder").Definition;
        Assert.Contains("push a branch", d, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("only when", d, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SeedHistory_ContainsTheHashOfEveryCurrentSeed()
    {
        var history = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<string>>>(
            File.ReadAllText(Path.Combine(SeedDir(), "persona-seed-history.json")))!;
        foreach (var p in Load())
            Assert.True(history.TryGetValue(p.Id, out var hashes) && hashes.Contains(HubPersonas.Hash(p.Name, p.Model, p.Definition)),
                $"{p.Id}: persona-seed-history.json is stale; run node scripts/seed-history.mjs and commit it");
    }

    /// <summary>
    /// Each persona is a skill of the fyrd plugin, <c>fyrd:&lt;id&gt;</c>, at the one depth the plugin loader scans. The
    /// plugin is installed environment-wide, so a session never loads a persona on its own: only a person runs it. The
    /// exceptions are the subagent personas (owner's decision): a coordinator's brief tells its subagent to load one with
    /// the Skill tool, which refuses a skill that sets <c>disable-model-invocation</c>.
    /// </summary>
    [Fact]
    public void EveryPersona_IsAPluginSkill_OnlyAPersonRuns()
    {
        var root = Path.GetDirectoryName(SeedDir())!;
        Assert.True(File.Exists(Path.Combine(root, ".claude-plugin", "plugin.json")));
        Assert.False(Directory.Exists(Path.Combine(root, "personas")), "personas live in skills/<id>/SKILL.md only");
        foreach (var id in Ids)
        {
            Assert.True(FrontMatter.TryParse(File.ReadAllText(Path.Combine(SeedDir(), id, "SKILL.md")), out var fields, out _), id);
            Assert.Equal(id, fields["name"]);
            Assert.Equal(HubPersonas.Kind, fields["kind"]);
            if (SubagentIds.Contains(id))
            {
                Assert.False(fields.ContainsKey("disable-model-invocation"), $"{id}: a subagent loads it with the Skill tool");
                Assert.Contains("when a brief tells you to act as", fields["description"], StringComparison.Ordinal);
            }
            else
                Assert.Equal("true", fields["disable-model-invocation"]);
            Assert.StartsWith("Fyrd persona: ", fields["description"], StringComparison.Ordinal);
            Assert.Contains($"/fyrd:{id}", fields["description"], StringComparison.Ordinal);
            Assert.InRange(fields["description"].Length, 1, SessionSkills.MaxDescriptionLength);
            // A skill's `model` would switch the session's model when it is run; the persona's model is Fyrd's setting.
            Assert.False(fields.ContainsKey("model"), id);
        }
    }

    /// <summary>The persona skills are neither session skills nor Controller request skills.</summary>
    [Fact]
    public void PersonaSkills_AreNoSessionOrRequestSkills()
    {
        Assert.DoesNotContain(RepoSeeds.SessionSkills(_home.Paths), s => Ids.Contains(s.Name));
        Assert.DoesNotContain(new HubSkills(SeedDir()).List(), s => Ids.Contains(s.Name));
    }

    [Fact]
    public void Controller_LoadsItsRequestSkillsFromThePlugin_WhenNoBlockCarriesThem()
    {
        var d = Get("controller").Definition;
        Assert.Contains("its `<controller-skill>` block when this message has one (the block wins over an installed skill of that name), else the fyrd plugin skill, loaded with the Skill tool as the `<controller-skills>` block says", d, StringComparison.Ordinal);
        Assert.Contains("or one `<controller-skills>` block naming the fyrd plugin skills to load instead", d, StringComparison.Ordinal);
        foreach (var name in new[] { "build", "research", "investigate", "watch", "tune", "peasant" })
        {
            Assert.Contains($"`fyrd:{name}`", d, StringComparison.Ordinal);
            Assert.DoesNotContain($"`fyrd-{name}`", d, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Controller_RoutesTheConsoleNudges()
    {
        var d = Get("controller").Definition;
        Assert.Contains("\"Validate PR <url>\" (from the owner)", d, StringComparison.Ordinal);
        Assert.Contains("PR #{n} is ready: <url>", d, StringComparison.Ordinal);
        Assert.Contains("Watch <url> and fix anything that comes up", d, StringComparison.Ordinal);
    }

    [Fact]
    public void Controller_MayCloneOnlyToRead()
    {
        var d = Get("controller").Definition;
        Assert.DoesNotContain("never clone a repo", d, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Never edit, commit or push from your clones", d, StringComparison.Ordinal);
        Assert.Contains("No repo", d, StringComparison.Ordinal);
        Assert.Contains("Base branch:", d, StringComparison.Ordinal);
    }

    [Fact]
    public void Controller_DispatchesButNeverRelaysBetweenPeasants()
    {
        var d = Get("controller").Definition;
        Assert.Contains("You dispatch; you don't coordinate.", d, StringComparison.Ordinal);
        Assert.Contains("You add no persona text for them to its `append_system_prompt`.", d, StringComparison.Ordinal);
        foreach (var relay in new[] { "Controller session:", "Validation request:", "Validation failed:", "Review request:" })
            Assert.DoesNotContain(relay, d, StringComparison.Ordinal);
    }

    /// <summary>A session gets its own persona and memory only; its persona subagents load their persona skills themselves.</summary>
    [Fact]
    public void Controller_CopiesTheWholePersona_AndNoSubagentText()
    {
        var d = Get("controller").Definition;
        Assert.DoesNotContain("subagent-persona", d, StringComparison.Ordinal);
        Assert.Contains("the persona's text exactly as it appears inside its `<peasant-persona>` block", d, StringComparison.Ordinal);
        // The close tags of the skills' examples reach the Controller escaped, so the persona says what a close tag looks like.
        Assert.Contains("A close tag starts with `</`; `<\\/` in what you received is only Fyrd's escaping, never write it in a tag of your own.", d, StringComparison.Ordinal);
    }

    /// <summary>
    /// A persona subagent loads its persona's plugin skill itself and follows only the sections its brief names. The
    /// whole persona still says to ask the owner (the Inputs rule) and to coordinate workers, so every brief overrides that.
    /// </summary>
    [Theory]
    [InlineData("builder", "Load the fyrd:<validator | reviewer | investigator> skill and follow only its <The check | Reviewing code | The investigation and As a subagent> sections and Working in Fyrd rules; if it won't load, return blocked. Ignore any instruction to ask the owner, post a review or message anyone; return your findings only.")]
    [InlineData("researcher", "Load the fyrd:reviewer skill and follow only its Reviewing a plan section and Working in Fyrd rules; if it won't load, return blocked. Ignore any instruction to ask the owner, post anything or message anyone; return your findings only.")]
    [InlineData("validator", "Load the fyrd:investigator skill and follow only its The investigation and As a subagent sections and Working in Fyrd rules; if it won't load, return blocked. Ignore any instruction to ask the owner, post anything or message anyone; return your findings only.")]
    [InlineData("monitor", "Load the fyrd:investigator skill and follow only its The investigation and As a subagent sections and Working in Fyrd rules; if it won't load, return blocked. Ignore any instruction to ask the owner, post anything or message anyone; return your findings only.")]
    public void EveryCoordinator_HasItsPersonaSubagentLoadItsSkill_AndIgnoreInstructionsToAskOrPost(string id, string line)
    {
        var d = Get(id).Definition;
        var section = d[d.IndexOf("# Persona subagents", StringComparison.Ordinal)..d.IndexOf("# Workers", StringComparison.Ordinal)];
        Assert.Contains($"`{line}`", section, StringComparison.Ordinal);
        Assert.DoesNotContain("subagent-persona", d, StringComparison.Ordinal);
    }

    /// <summary>The sections a brief names are headings of the persona the subagent loads; a renamed one breaks the brief.</summary>
    [Theory]
    [InlineData("validator", new[] { "The check", "Working in Fyrd" })]
    [InlineData("reviewer", new[] { "Reviewing code", "Reviewing a plan", "Working in Fyrd" })]
    [InlineData("investigator", new[] { "The investigation", "As a subagent", "Working in Fyrd" })]
    public void TheSectionsABriefNames_AreHeadingsOfThePersona(string id, string[] headings)
    {
        var lines = Get(id).Definition.ReplaceLineEndings("\n").Split('\n');
        foreach (var h in headings)
            Assert.Equal(1, lines.Count(l => l == "# " + h));
    }

    /// <summary>The Builder is told outright what its persona subagents don't do; asking the owner is the Builder's.</summary>
    [Fact]
    public void Builder_SaysAPersonaSubagentNeverAsksTheOwner()
    {
        var d = Get("builder").Definition;
        var section = d[d.IndexOf("# Persona subagents", StringComparison.Ordinal)..d.IndexOf("# Workers", StringComparison.Ordinal)];
        Assert.Contains("A persona subagent never asks the owner,", section, StringComparison.Ordinal);
    }

    [Fact]
    public void Builder_PushesAsItGoes_AndKnowsItsBase()
    {
        var d = Get("builder").Definition;
        Assert.Contains("git push -u origin", d, StringComparison.Ordinal);
        Assert.Contains("after every commit", d, StringComparison.Ordinal);
        Assert.Contains("origin/HEAD", d, StringComparison.Ordinal);
        Assert.Contains("subscribe_pr_activity", d, StringComparison.Ordinal);
    }

    [Fact]
    public void Builder_ValidatesAndReviewsItsOwnPrsWithSubagents()
    {
        var d = Get("builder").Definition;
        Assert.Contains("Load the fyrd:<validator | reviewer | investigator> skill", d, StringComparison.Ordinal);
        Assert.Contains("subagent_type: \"general-purpose\"", d, StringComparison.Ordinal);
        Assert.Contains("markPullRequestReadyForReview", d, StringComparison.Ordinal);
        Assert.Contains("event: COMMENT", d, StringComparison.Ordinal);
        Assert.Contains("return your findings only", d, StringComparison.Ordinal);
        Assert.Contains("authored by you: it is not new work", d, StringComparison.Ordinal);
        foreach (var relay in new[] { "Controller session:", "Validation request:", "Validation failed:", "Review request:" })
            Assert.DoesNotContain(relay, d, StringComparison.Ordinal);
    }

    [Fact]
    public void Builder_StacksMultiPrWorkNativelyThroughTheApi()
    {
        var d = Whole("builder");
        Assert.Contains("POST /repos/{owner}/{repo}/stacks", d, StringComparison.Ordinal);
        Assert.Contains("POST /repos/{owner}/{repo}/stacks/{stack_number}/add", d, StringComparison.Ordinal);
        Assert.Contains("POST /repos/{owner}/{repo}/merges", d, StringComparison.Ordinal);
        Assert.Contains("X-GitHub-Api-Version: 2026-03-10", d, StringComparison.Ordinal);
        Assert.Contains("Never force-push, never push", d, StringComparison.Ordinal);
        Assert.Contains("any non-2xx response", d, StringComparison.Ordinal);
    }

    [Fact]
    public void NoPersonaShellsOutToTheGhCli()
    {
        foreach (var (id, text) in Load().Select(p => (p.Id, Whole(p.Id))))
            foreach (var cli in new[] { "`gh pr", "`gh stack", "gh extension", "`gh api" })
                Assert.False(text.Contains(cli, StringComparison.Ordinal), $"{id} shells out to the gh CLI ({cli})");
    }

    [Fact]
    public void Validator_ReportsInItsReply_NotToTheController()
    {
        var d = Get("validator").Definition;
        Assert.Contains("# The check", d, StringComparison.Ordinal);
        Assert.Contains("You are a worker for Builder", d, StringComparison.Ordinal);
        foreach (var relay in new[] { "Controller session:", "Validation failed:", "Review request:" })
            Assert.DoesNotContain(relay, d, StringComparison.Ordinal);
    }

    [Fact]
    public void Reviewer_ReviewsCodeAndPlans()
    {
        var d = Get("reviewer").Definition;
        Assert.Contains("# Reviewing code", d, StringComparison.Ordinal);
        Assert.Contains("# Reviewing a plan", d, StringComparison.Ordinal);
        Assert.Contains("ready to build", d, StringComparison.Ordinal);
    }

    [Fact]
    public void Researcher_AsksDeeply_AndReviewsThePlanBeforePublishing()
    {
        var d = Get("researcher").Definition;
        Assert.Contains("Load the fyrd:reviewer skill", d, StringComparison.Ordinal);
        Assert.Contains("Go deep on each work item", d, StringComparison.Ordinal);
        Assert.True(d.IndexOf("**Review the plan.**", StringComparison.Ordinal) < d.IndexOf("**Publish.**", StringComparison.Ordinal),
            "the plan must be reviewed before it is published");
    }

    [Fact]
    public void Peasants_AskTheOwnerDirectly()
    {
        foreach (var p in Load().Where(p => p.Id != "controller"))
            Assert.True(p.Definition.Contains("Never route a question through the Controller", StringComparison.Ordinal), $"{p.Id} may route questions");
    }

    [Fact]
    public void EveryPersona_HasTheSameDraftPullRequestBullet()
    {
        static string Bullet(string d) => d.Split('\n').SingleOrDefault(l => l.StartsWith("- **Pull requests.**", StringComparison.Ordinal)) ?? "";
        var all = Load();
        var expected = Bullet(all[0].Definition);
        Assert.Contains("`draft: true`", expected, StringComparison.Ordinal);
        foreach (var p in all) Assert.Equal(expected, Bullet(p.Definition));
    }

    [Fact]
    public void Reviewer_PostsOneInlineReview_NeverApproves()
    {
        var d = Get("reviewer").Definition;
        Assert.Contains("add_comment_to_pending_review", d, StringComparison.Ordinal);
        Assert.Contains("submit_pending", d, StringComparison.Ordinal);
        Assert.Contains("event: COMMENT", d, StringComparison.Ordinal);
        Assert.Contains("Never approve or request changes", d, StringComparison.Ordinal);
    }

    [Fact]
    public void Researcher_CommitsThePlanByDefault()
    {
        var d = Get("researcher").Definition;
        Assert.DoesNotContain("never commit", d, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Commit it and push the branch", d, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryPeasantMentions_ArtifactTool()
    {
        foreach (var p in Load().Where(p => p.Id != "controller"))
            Assert.True(p.Definition.Contains("Artifact tool's `read` action", StringComparison.Ordinal), $"{p.Id} lacks the Artifact tool");
    }

    [Fact]
    public void Controller_CarriesTeardownCapAndLeadRules()
    {
        var d = Get("controller").Definition;
        foreach (var s in new[] { "Never archive yourself", "`Start fewer now`", "`Tear down idle sessions`", "`Cancel`", "The Lead role isn't defined yet", "delete_trigger", "Before archiving or creating anything", "`multiSelect: true` `AskUserQuestion`", "watching" })
            Assert.Contains(s, d, StringComparison.Ordinal);
    }

    [Fact]
    public void Controller_InstructionsArriveAsAMessage_NotTheSystemPrompt()
    {
        var d = Get("controller").Definition;
        Assert.DoesNotContain("system prompt", d, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("this prompt", d, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("as a message", d, StringComparison.Ordinal);
    }

    [Fact]
    public void Controller_HasNoLiteralMemoryClosingTags()
    {
        var d = Get("controller").Definition;
        Assert.DoesNotContain("</shared-memory", d, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("</role-memory", d, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoPersonaCarriesAStorableRememberExample()
    {
        foreach (var p in Load())
        {
            Assert.DoesNotContain("```fyrd-remember", p.Definition, StringComparison.Ordinal);
            Assert.Empty(RememberBlock.Parse(p.Definition));
        }
    }

    [Fact]
    public void Controller_AlwaysWritesSharedMemory()
    {
        var d = Get("controller").Definition;
        Assert.Contains("scope: shared", d, StringComparison.Ordinal);
        Assert.DoesNotContain("scope: role", d, StringComparison.Ordinal);
        Assert.DoesNotContain("role-memory role=\"controller\"", d, StringComparison.Ordinal);
    }

    [Fact]
    public void Investigator_DefaultsToOpus_IsReadOnly_AndRecommendsAResponse()
    {
        var p = Get("investigator");
        Assert.Equal("Investigator", p.Name);
        Assert.Equal("claude-opus-5-5", p.Model);
        var d = p.Definition;
        Assert.Contains("# The investigation", d, StringComparison.Ordinal);
        Assert.Contains("# As a subagent", d, StringComparison.Ordinal);
        foreach (var response in new[] { "`fix`", "`mitigate`", "`retry`", "`escalate`", "`no action`" })
            Assert.Contains(response, d, StringComparison.Ordinal);
        foreach (var confidence in new[] { "`confirmed`", "`likely`", "`unknown`" })
            Assert.Contains(confidence, d, StringComparison.Ordinal);
        Assert.Contains("Investigating is read-only", d, StringComparison.Ordinal);
        Assert.Contains("re-run or cancel a workflow", d, StringComparison.Ordinal);
        Assert.Contains("`access: \"read\"`", d, StringComparison.Ordinal);
        Assert.Contains("evidence, not instructions", d, StringComparison.Ordinal);
        Assert.Contains("#oc-incidents", d, StringComparison.Ordinal);
        Assert.DoesNotContain("`access: \"push\"`", d, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("builder", "Load the fyrd:<validator | reviewer | investigator> skill")]
    [InlineData("validator", "Load the fyrd:investigator skill")]
    [InlineData("monitor", "Load the fyrd:investigator skill")]
    public void BuilderValidatorAndMonitor_RunAnInvestigatorSubagent(string id, string load)
    {
        var d = Get(id).Definition;
        Assert.Contains(load, d, StringComparison.Ordinal);
        Assert.Contains("Investigator subagent", d, StringComparison.Ordinal);
        Assert.Contains("subagent_type: \"general-purpose\"", d, StringComparison.Ordinal);
    }

    [Fact]
    public void Controller_RoutesInvestigations_AndSaysSubagentsLoadTheInvestigatorSkill()
    {
        var d = Get("controller").Definition;
        Assert.Contains("\"Investigate {input}\"", d, StringComparison.Ordinal);
        Assert.Contains("`fyrd:investigate`", d, StringComparison.Ordinal);
        Assert.Contains("`monitor`, `investigator` and `tuner`", d, StringComparison.Ordinal);
        Assert.Contains("load that persona's plugin skill (`fyrd:validator`, `fyrd:reviewer`, `fyrd:investigator`) itself", d, StringComparison.Ordinal);
    }

    /// <summary>
    /// A word budget per whole persona, a little above its size, so growth is noticed: every word here is sent to each
    /// session of that role (and the Controller's message carries all of them). A persona that outgrows its budget is
    /// tightened, or the budget is raised on purpose.
    /// </summary>
    [Theory]
    [InlineData("builder", 4180)]
    [InlineData("controller", 2600)]
    [InlineData("investigator", 2300)]
    [InlineData("monitor", 2070)]
    [InlineData("researcher", 2430)]
    [InlineData("reviewer", 2780)]
    [InlineData("tuner", 2800)]
    [InlineData("validator", 2790)]
    public void EveryPersona_StaysUnderItsWordBudget(string id, int budget)
    {
        var words = Get(id).Definition.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        Assert.True(words <= budget, $"{id}: {words} words, over the budget of {budget}. Tighten the persona, or raise its budget in this test on purpose (a guard change to list in the PR).");
    }

    [Fact]
    public void EveryPersonaHasAWordBudget()
    {
        var budgeted = typeof(HubSeedTests).GetMethod(nameof(EveryPersona_StaysUnderItsWordBudget))!
            .GetCustomAttributesData().Where(a => a.AttributeType == typeof(InlineDataAttribute))
            .Select(a => (string)((IEnumerable<System.Reflection.CustomAttributeTypedArgument>)a.ConstructorArguments[0].Value!).First().Value!);
        Assert.Equal(Ids, budgeted.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Tuner_SchedulesItselfDaily_ScopesRuns_AndOnlyProposesInAnIssue()
    {
        var p = Get("tuner");
        Assert.Equal("Tuner", p.Name);
        var d = p.Definition;
        Assert.Contains("create_trigger", d, StringComparison.Ordinal);
        Assert.Contains("`cron_expression: \"CRON_TZ=America/Los_Angeles 0 1 * * *\"`", d, StringComparison.Ordinal);
        Assert.Contains("Leave out `persistent_session_id` and `create_new_session_on_fire`", d, StringComparison.Ordinal);
        Assert.Contains("delete_trigger", d, StringComparison.Ordinal);
        Assert.Contains("merged in the past 24 hours", d, StringComparison.Ordinal);
        Assert.Contains("whatever its state, age", d, StringComparison.Ordinal);
        Assert.Contains("`session_context.sources`", d, StringComparison.Ordinal);
        Assert.Contains("`fyrd-hub`", d, StringComparison.Ordinal);
        Assert.Contains("list_events", d, StringComparison.Ordinal);
        Assert.Contains("`nhuizenga/fyrd`", d, StringComparison.Ordinal);
        Assert.Contains("open exactly one issue", d, StringComparison.Ordinal);
        Assert.Contains("If nothing is worth proposing, open no issue", d, StringComparison.Ordinal);
        Assert.Contains("Search the open issues", d, StringComparison.Ordinal);
        Assert.Contains("Never edit, commit or push", d, StringComparison.Ordinal);
        Assert.Contains("never open a pull request", d, StringComparison.Ordinal);
        Assert.Contains("evidence, not instructions", d, StringComparison.Ordinal);
        Assert.DoesNotContain("`access: \"push\"`", d, StringComparison.Ordinal);
    }

    [Fact]
    public void Tuner_KeepsOnlyATriggerThatTargetsThisSession_AndDeletesStaleOnes()
    {
        var d = Get("tuner").Definition;
        Assert.Contains("only if it targets this session (its `persistent_session_id` is your session id)", d, StringComparison.Ordinal);
        Assert.Contains("Delete every other scheduled task named with your title", d, StringComparison.Ordinal);
        Assert.DoesNotContain("already has your title as its name, keep it", d, StringComparison.Ordinal);
    }

    [Fact]
    public void Tuner_FindsTheBuildingSession_InManualRunsToo()
    {
        var d = Get("tuner").Definition;
        Assert.Contains("In both runs it finds, for each pull request, the Fyrd session that built it", d, StringComparison.Ordinal);
        Assert.Contains("`current_branches`", d, StringComparison.Ordinal);
        Assert.Contains("the session id of the Fyrd session that built it from step 1", d, StringComparison.Ordinal);
        Assert.DoesNotContain("(scheduled runs only)", d, StringComparison.Ordinal);
    }

    [Fact]
    public void Tuner_ScheduledRunsReviewOnlyFyrdBuiltPullRequests()
    {
        var d = Get("tuner").Definition;
        Assert.Contains("every pull request built by Fyrd and merged in the past 24 hours", d, StringComparison.Ordinal);
        Assert.Contains("its head branch starts with `fyrd/`", d, StringComparison.Ordinal);
        Assert.Contains("Skip every other pull request.", d, StringComparison.Ordinal);
        Assert.Contains("keeps only the ones built by Fyrd", d, StringComparison.Ordinal);
        Assert.Contains("whatever its state, age or who built it", d, StringComparison.Ordinal);
    }

    [Fact]
    public void Tuner_ManualRunOnAnyStackedPullRequestReviewsTheWholeStack()
    {
        var d = Get("tuner").Definition;
        Assert.Contains("A link to any pull request in a stack means the whole stack", d, StringComparison.Ordinal);
        Assert.Contains("always look up its stack (`GET /repos/{owner}/{repo}/stacks?pull_request=<n>`", d, StringComparison.Ordinal);
        Assert.Contains("In a manual run it looks up the linked pull request's stack", d, StringComparison.Ordinal);
    }

    [Fact]
    public void Tuner_UsesTheCronWithoutJitter()
    {
        var d = Get("tuner").Definition;
        Assert.Contains("Use this cron exactly as given, without jitter", d, StringComparison.Ordinal);
    }

    [Fact]
    public void Tuner_WorkersReadPersonasAndSkillsAtOriginMain()
    {
        var d = Get("tuner").Definition;
        Assert.Contains("run `git fetch origin main` first and read every persona and skill at `origin/main`", d, StringComparison.Ordinal);
        Assert.Contains("`git show origin/main:skills/builder/SKILL.md`", d, StringComparison.Ordinal);
        Assert.Contains("never the working tree", d, StringComparison.Ordinal);
        Assert.Contains("already handled in the current text (at `origin/main`", d, StringComparison.Ordinal);
        Assert.Contains("the exact current wording (at `origin/main`)", d, StringComparison.Ordinal);
    }

    [Fact]
    public void Tuner_BuildingSessionLookupHasAPrecedence_AndNoTimeLimit()
    {
        var d = Get("tuner").Definition;
        var link = d.IndexOf("(1) the `claude.ai/code/session_…` link in the pull request's body, confirmed with `get_session` and its `fyrd-hub` tag", StringComparison.Ordinal);
        var branch = d.IndexOf("(2) a session whose `current_branches` equals the head branch", StringComparison.Ordinal);
        var title = d.IndexOf("(3) a session whose title matches only when the head branch is exactly `fyrd/<title>` or `fyrd/<title>-<suffix>`", StringComparison.Ordinal);
        Assert.True(link > 0 && branch > link && title > branch, "lookup precedence must be body link, then current_branches, then title");
        Assert.Contains("`fyrd/builder-20` doesn't match `builder-2`", d, StringComparison.Ordinal);
        Assert.Contains("take the latest one created before the pull request was opened", d, StringComparison.Ordinal);
        Assert.Contains("This lookup has no time limit in either run", d, StringComparison.Ordinal);
        Assert.Contains("Filter the `list_sessions` rows by title before calling `get_session`", d, StringComparison.Ordinal);
        Assert.DoesNotContain("whose title matches the head branch `fyrd/<title>…`", d, StringComparison.Ordinal);
    }

    [Fact]
    public void Tuner_CapsTranscriptReading()
    {
        var d = Get("tuner").Definition;
        Assert.Contains("`limit: 100`, and page with `before_id`, newest first, at most 5 pages", d, StringComparison.Ordinal);
        Assert.Contains("Read every `user` turn", d, StringComparison.Ordinal);
        Assert.Contains("around owner corrections, errors and retries", d, StringComparison.Ordinal);
        Assert.Contains("say so in your result", d, StringComparison.Ordinal);
    }

    [Fact]
    public void Tuner_DuplicateCheckCoversNotPlannedIssues()
    {
        var d = Get("tuner").Definition;
        Assert.Contains("closed as not planned (`state_reason: not_planned`)", d, StringComparison.Ordinal);
        Assert.Contains("or a not-planned one already turned down, and name that issue in your reply", d, StringComparison.Ordinal);
    }

    [Fact]
    public void Tuner_HandlesABareStackLink()
    {
        var d = Get("tuner").Definition;
        Assert.Contains("A bare stack link (`https://github.com/<owner>/<repo>/stacks/<number>`)", d, StringComparison.Ordinal);
        Assert.Contains("`GET /repos/{owner}/{repo}/stacks/{number}`", d, StringComparison.Ordinal);
        Assert.Contains("`Tuner: <owner>/<repo> stack <number>`", d, StringComparison.Ordinal);
    }

    [Fact]
    public void Controller_RoutesTheTuner_AndDeletesItsTriggerOnTeardown()
    {
        var d = Get("controller").Definition;
        Assert.Contains("\"Tune <PR or stack url>\"", d, StringComparison.Ordinal);
        Assert.Contains("`fyrd:tune`", d, StringComparison.Ordinal);
        Assert.Contains("For a Monitor or the Tuner, first delete its scheduled task", d, StringComparison.Ordinal);
    }

    [Fact]
    public void NoPlaceholdersLeft()
    {
        foreach (var f in Directory.EnumerateFiles(SeedDir(), "SKILL.md", SearchOption.AllDirectories))
            Assert.DoesNotContain("TODO(Task 10)", File.ReadAllText(f), StringComparison.Ordinal);
    }
}

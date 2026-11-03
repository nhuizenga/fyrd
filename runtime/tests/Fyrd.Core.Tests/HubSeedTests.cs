using Fyrd.Core.Hub;

namespace Fyrd.Core.Tests;

/// <summary>Checks the checked-in hub persona seeds under <c>personas/hub</c>.</summary>
public sealed class HubSeedTests : IDisposable
{
    private static readonly string[] Ids = ["builder", "controller", "monitor", "researcher", "reviewer", "validator"];
    private readonly TempHome _home = new();

    public void Dispose() => _home.Dispose();

    private static string SeedDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "personas", "hub");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("personas/hub not found above " + AppContext.BaseDirectory);
    }

    private IReadOnlyList<HubPersona> Load() => new HubPersonas(_home.Paths, SeedDir()).List();

    private HubPersona Get(string id) => Load().Single(p => p.Id == id);

    [Fact]
    public void AllSixSeedsParse()
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
            File.ReadAllText(Path.Combine(SeedDir(), "seed-history.json")))!;
        foreach (var p in Load())
            Assert.True(history.TryGetValue(p.Id, out var hashes) && hashes.Contains(HubPersonas.Hash(p.Name, p.Model, p.Definition)),
                $"{p.Id}: seed-history.json is stale; run node scripts/seed-history.mjs and commit it");
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
        Assert.Contains("`subagent-persona` element with `id=\"validator\"`", d, StringComparison.Ordinal);
        foreach (var relay in new[] { "Controller session:", "Validation request:", "Validation failed:", "Review request:" })
            Assert.DoesNotContain(relay, d, StringComparison.Ordinal);
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
        Assert.Contains("<subagent-persona id=\"validator\">", d, StringComparison.Ordinal);
        Assert.Contains("<subagent-persona id=\"reviewer\">", d, StringComparison.Ordinal);
        Assert.Contains("subagent_type: \"general-purpose\"", d, StringComparison.Ordinal);
        Assert.Contains("markPullRequestReadyForReview", d, StringComparison.Ordinal);
        Assert.Contains("event: COMMENT", d, StringComparison.Ordinal);
        foreach (var relay in new[] { "Controller session:", "Validation request:", "Validation failed:", "Review request:" })
            Assert.DoesNotContain(relay, d, StringComparison.Ordinal);
    }

    [Fact]
    public void Builder_StacksMultiPrWorkNativelyThroughTheApi()
    {
        var d = Get("builder").Definition;
        Assert.Contains("POST /repos/{owner}/{repo}/stacks", d, StringComparison.Ordinal);
        Assert.Contains("POST /repos/{owner}/{repo}/stacks/{stack_number}/add", d, StringComparison.Ordinal);
        Assert.Contains("POST /repos/{owner}/{repo}/merges", d, StringComparison.Ordinal);
        Assert.Contains("X-GitHub-Api-Version: 2026-03-10", d, StringComparison.Ordinal);
        Assert.Contains("Never force-push, never push", d, StringComparison.Ordinal);
    }

    [Fact]
    public void NoPersonaShellsOutToTheGhCli()
    {
        foreach (var (id, text) in Load().Select(p => (p.Id, p.Definition)))
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
        Assert.Contains("<subagent-persona id=\"reviewer\">", d, StringComparison.Ordinal);
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
    public void NoPlaceholdersLeft()
    {
        foreach (var f in Directory.EnumerateFiles(SeedDir(), "*.md"))
            Assert.DoesNotContain("TODO(Task 10)", File.ReadAllText(f), StringComparison.Ordinal);
    }
}

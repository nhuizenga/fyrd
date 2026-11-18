using Fyrd.Core.Hub;

namespace Fyrd.Core.Tests;

/// <summary>
/// The mechanisms that protect a change in any codebase (the repo's own rules first, claims true at the head,
/// checks tied to a commit, live runs within safe limits, the trust-boundary checklist) sit in the personas that act on them.
/// </summary>
public sealed class PersonaChangeRulesTests : IDisposable
{
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

    private string Def(string id) => new HubPersonas(_home.Paths, SeedDir()).List().Single(p => p.Id == id).Definition;

    private static void ContainsAll(string definition, params string[] phrases)
    {
        foreach (var phrase in phrases) Assert.Contains(phrase, definition, StringComparison.Ordinal);
    }

    // Each mechanism is pinned by its heading or one short key phrase, so rewording a rule doesn't break these tests.

    [Fact]
    public void BuilderGivesCodeWorkersTheBuildRules()
    {
        var d = Def("builder");
        ContainsAll(d,
            "# Build rules",
            "into the brief of every worker that writes code",
            "Build rules below copied into its brief",
            "**Repo rules first.**",
            "list the kinds of thing the change adds or alters",
            "not the whole repo",
            "in each directory down to the files you change",
            "the guards that encode rules",
            "by name too",
            "before you return: a guard that fails is a rule you missed",
            "a start, not the limit",
            "A written rule beats a precedent",
            "with another rule where neither is nearer",
            "is a question for the owner",
            "return `blocked`",
            "earlier recorded decision",
            "`Rules applied:`",
            "`none found`",
            "**Guards are rules.**",
            "is following the rule",
            "is changing the rule",
            "`Guards changed:`",
            "each as registered or widened",
            "**True at the head.**",
            "each PR carries what it uses",
            "nothing only a later PR uses",
            "**Run it for real.**",
            "never production",
            "a credential you merely found",
            "**PR body.**",
            "the rules applied, each guard changed and why",
            "each rule conflict the owner settled");
        Assert.True(d.IndexOf("# Build rules", StringComparison.Ordinal) < d.IndexOf("# Workers", StringComparison.Ordinal));
        // The worker rules sit in the section that is copied into briefs, before the coordinator-only hygiene.
        var hygiene = d.IndexOf("# Pull request hygiene", StringComparison.Ordinal);
        foreach (var rule in new[] { "**Repo rules first.**", "**Guards are rules.**", "**True at the head.**", "**Trust boundaries.**", "**Run it for real.**" })
            Assert.InRange(d.IndexOf(rule, StringComparison.Ordinal), d.IndexOf("# Build rules", StringComparison.Ordinal), hygiene);
        // The lists survive fix rounds and a stack, and reach the report.
        ContainsAll(d,
            "add its PR's current body to the brief",
            "add the body the worker below returned",
            "lists only the rules and guards its own diff touches",
            "keeping the earlier rules, guards, settled conflicts and exceptions that still hold",
            "the repo rules applied, each guard changed");
    }

    [Fact]
    public void BuilderBuildRulesCoverTrustBoundaries()
    {
        ContainsAll(Def("builder"),
            "**Trust boundaries.**",
            "new caller or constructor",
            "final, transformed value",
            "rate limit",
            "someone else's resource",
            "privileged identity",
            "sensitive egress");
    }

    [Fact]
    public void BuilderKeepsPullRequestsHonest()
    {
        var d = Def("builder");
        ContainsAll(d,
            "# Pull request hygiene",
            "**Body matches the diff.**",
            "carries the worker's rules applied",
            "**Moved work.**",
            "both PR bodies",
            "**Current head.**",
            "**Answer every thread.**",
            "`Fixed in <sha>`",
            "its first comment is one you posted in step 7",
            "the author's login proves nothing",
            "Never resolve anyone else's thread",
            "A pass counts only for the head commit",
            "validate each changed PR once",
            "only about the PR body",
            "leaves the new head unreviewed",
            "confirms every earlier **Blocking** and **Should fix** finding fixed",
            "after that second re-review stop",
            "counts as one of step 7's rounds",
            "whose last review is on an older head",
            "not re-reviewed unless the owner asks",
            "never run live");
        // The existing rules stay.
        ContainsAll(d, "Never force-push", "Never resolve a reviewer's thread for them");
    }

    [Fact]
    public void ReviewerDerivesTheRepoRulesAndHasTheChangeChecklist()
    {
        var d = Def("reviewer");
        ContainsAll(d,
            "**Repo rules.**",
            "derive the rules yourself",
            "in each directory down to the changed files",
            "a possible guard change",
            "tell registering new code from widening",
            "a rule the PR body's list missed",
            "only where the body has such a list",
            "between two rules where neither is nearer",
            "doesn't show the owner settled",
            "earlier recorded decision",
            "that the PR body doesn't justify",
            "duplicate a mechanism it already has",
            "that the change touches",
            "**Trust boundaries.**",
            "**Checks on the final value.**",
            "**New endpoints.**",
            "**Authorization tests.**",
            "**Privileged identities.**",
            "**Egress and records.**",
            "**True at the head.**",
            "**Never run.**",
            "Never ask for a run against production",
            "the head commit reviewed",
            "what your own review did not do",
            "reply on your earlier thread");
        Assert.True(d.IndexOf("**Repo rules.**", StringComparison.Ordinal) < d.IndexOf("**Trust boundaries.**", StringComparison.Ordinal));
        Assert.True(d.IndexOf("**Trust boundaries.**", StringComparison.Ordinal) < d.IndexOf("# Reviewing a plan", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatorChecksTheHeadTheClaimsAndTheBoundaries()
    {
        ContainsAll(Def("validator"),
            "note that commit",
            "in a browser",
            "never production",
            "the PR title and body describe this diff",
            "isn't in the repo at this commit",
            "each repo rule the PR body lists as applied",
            "the diff meets it as claimed",
            "if the body lists none, say so",
            "named and justified in the PR body",
            "needs no justification",
            "someone else's resource",
            "Never probe production",
            "governs every live run in this step too",
            "a credential you merely found",
            "Return the head commit you checked",
            "could not run live",
            "still the commit you checked");
    }

    [Fact]
    public void PlansNameTheRepoRulesLiveRunsAndRationale()
    {
        ContainsAll(Def("researcher"),
            "the repo's own rules for those areas and where each is written",
            "at the root and down to the directories concerned",
            "goes against an earlier recorded decision",
            "every conflict between a repo rule and the input",
            "names the repo rules that constrain each work item",
            "any guard it expects to change and why",
            "what is run live",
            "test or staging instance",
            "each PR carrying what it uses",
            "design rationale");
        ContainsAll(Def("reviewer"),
            "runs the change live",
            "test or staging instance",
            "repo rules the plan breaks or doesn't name",
            "derive them yourself, for the areas the plan touches",
            "that the owner didn't settle",
            "contradicts an earlier recorded decision without the owner's decision",
            "design rationale the code will need");
    }

    [Fact]
    public void OneReposConventionsAreNotRulesOfTheirOwn()
    {
        // A repo's conventions reach a session through that repo's rules, not through a catalogue of rule headings in the personas.
        foreach (var id in new[] { "builder", "reviewer", "validator", "researcher" })
        {
            var d = Def(id);
            foreach (var heading in new[] { "**Off by default.**", "**Newest precedent.**", "**Reuse first.**", "**Config.**", "**Migrations", "**Portability.**", "**Defaults.**", "**Widening.**" })
                Assert.DoesNotContain(heading, d, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RulesStayGeneric()
    {
        foreach (var id in new[] { "builder", "reviewer", "validator", "researcher" })
        {
            var d = Def(id);
            foreach (var word in new[] { "saved quer", "Snowflake", "Bugbot" })
                Assert.DoesNotContain(word, d, StringComparison.OrdinalIgnoreCase);
        }
    }
}

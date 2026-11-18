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
            "not the whole repo",
            "in each directory down to the files you change",
            "the guards that encode rules",
            "A written rule beats a precedent",
            "is a question for the owner",
            "`Rules applied:`",
            "**Guards are rules.**",
            "`Guards changed:`",
            "**True at the head.**",
            "each PR carries what it uses",
            "**Run it for real.**",
            "never production",
            "a credential you merely found",
            "**PR body.**",
            "the rules applied, each guard changed and why");
        Assert.True(d.IndexOf("# Build rules", StringComparison.Ordinal) < d.IndexOf("# Workers", StringComparison.Ordinal));
        // The worker rules sit in the section that is copied into briefs, before the coordinator-only hygiene.
        var hygiene = d.IndexOf("# Pull request hygiene", StringComparison.Ordinal);
        foreach (var rule in new[] { "**Repo rules first.**", "**Guards are rules.**", "**True at the head.**", "**Trust boundaries.**", "**Run it for real.**" })
            Assert.InRange(d.IndexOf(rule, StringComparison.Ordinal), d.IndexOf("# Build rules", StringComparison.Ordinal), hygiene);
        // The coordinator passes the lists on.
        ContainsAll(d,
            "You put the rules applied, the guards changed and the exceptions in the PR body and your report",
            "carries the worker's rules applied and guards changed",
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
            "sensitive egress",
            "earlier recorded decision");
    }

    [Fact]
    public void BuilderKeepsPullRequestsHonest()
    {
        var d = Def("builder");
        ContainsAll(d,
            "# Pull request hygiene",
            "**Body matches the diff.**",
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
            "a rule the PR body's list missed",
            "settled without the owner",
            "that the PR body doesn't justify",
            "that the change touches",
            "**Trust boundaries.**",
            "**Checks on the final value.**",
            "**New endpoints.**",
            "**Authorization tests.**",
            "**Privileged identities.**",
            "**Egress and records.**",
            "**Decisions.**",
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
            "named and justified in the PR body",
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
            "that the owner didn't settle",
            "design rationale the code will need");
    }

    [Fact]
    public void OneReposConventionsAreNotRulesOfTheirOwn()
    {
        // A repo's conventions reach a session through that repo's rules, not through a catalogue in the personas.
        foreach (var id in new[] { "builder", "reviewer", "validator", "researcher" })
        {
            var d = Def(id);
            foreach (var phrase in new[] { "Off by default", "off by default", "grandfathered", "backfilled", "Newest precedent", "Reuse first", "export and import", "environment variable", "sequence number" })
                Assert.DoesNotContain(phrase, d, StringComparison.Ordinal);
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

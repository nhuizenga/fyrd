using Fyrd.Core.Hub;

namespace Fyrd.Core.Tests;

/// <summary>The build, pull request hygiene and trust-boundary rules sit in the personas that act on them.</summary>
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

    // Each rule is pinned by its heading or one short key phrase, so rewording a rule doesn't break these tests.

    [Fact]
    public void BuilderGivesCodeWorkersTheBuildRules()
    {
        var d = Def("builder");
        ContainsAll(d,
            "# Build rules",
            "into the brief of every worker that writes code",
            "Build rules below copied into its brief",
            "**Newest precedent.**",
            "A precedent never overrides",
            "**Off by default.**",
            "**Reuse first.**",
            "**Fix, don't widen.**",
            "environment-specific name or literal",
            "**Point only at what exists.**",
            "**Config.**",
            "**Migrations and other ordered files**",
            "**Each PR carries what it uses.**",
            "**Run it for real.**",
            "never production",
            "a credential you merely found",
            "**PR body.**");
        Assert.True(d.IndexOf("# Build rules", StringComparison.Ordinal) < d.IndexOf("# Workers", StringComparison.Ordinal));
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
            "export and import",
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
            "that review's id",
            "Never resolve anyone else's thread",
            "A pass counts only for the head commit",
            "validate each changed PR once",
            "only about the PR body",
            "leaves the new head unreviewed",
            "after that second re-review stop",
            "whose last review is on an older head",
            "not re-reviewed unless the owner asks",
            "never run live");
        // The existing rules stay.
        ContainsAll(d, "Never force-push", "Never resolve a reviewer's thread for them");
    }

    [Fact]
    public void ReviewerHasTheChangeChecklist()
    {
        var d = Def("reviewer");
        ContainsAll(d,
            "that the change touches",
            "**Trust boundaries.**",
            "**Checks on the final value.**",
            "**New endpoints.**",
            "**Authorization tests.**",
            "**Privileged identities.**",
            "**Egress and records.**",
            "**Portability.**",
            "**Decisions.**",
            "**Defaults.**",
            "**Widening.**",
            "**What exists.**",
            "**Never run.**",
            "Never ask for a run against production",
            "the head commit reviewed",
            "what your own review did not do",
            "reply on your earlier thread");
        Assert.True(d.IndexOf("**Trust boundaries.**", StringComparison.Ordinal) < d.IndexOf("# Reviewing a plan", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatorChecksTheHeadTheClaimsAndTheBoundaries()
    {
        ContainsAll(Def("validator"),
            "note that commit",
            "in a browser",
            "never production",
            "the PR body describes this diff",
            "off by default",
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
    public void PlansSettleDefaultsLiveRunsAndRationale()
    {
        ContainsAll(Def("researcher"),
            "not grandfathered",
            "what is run live",
            "test or staging instance",
            "newest precedent",
            "design rationale");
        ContainsAll(Def("reviewer"),
            "runs the change live",
            "test or staging instance",
            "grandfathered",
            "design rationale the code will need");
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

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

    [Fact]
    public void BuilderGivesCodeWorkersTheBuildRules()
    {
        var d = Def("builder");
        ContainsAll(d,
            "# Build rules",
            "Copy this section into the brief of every worker that writes code",
            "with the Build rules below copied into its brief",
            "**Newest precedent.**",
            "**Off by default.**",
            "backfilled from an existing one",
            "**Reuse first.**",
            "**Fix, don't widen.**",
            "**Point only at what exists.**",
            "lives only on an unmerged branch or outside the repo",
            "Register each new one everywhere the repo registers config",
            "never a later one",
            "**Each PR carries what it uses.**",
            "**Run it for real.**",
            "**PR body.**");
        Assert.True(d.IndexOf("# Build rules", StringComparison.Ordinal) < d.IndexOf("# Workers", StringComparison.Ordinal));
    }

    [Fact]
    public void BuilderBuildRulesCoverTrustBoundaries()
    {
        ContainsAll(Def("builder"),
            "**Trust boundaries.**",
            "fails when a new caller or constructor appears",
            "Run checks on the final, transformed value",
            "a cheap rate limit before any expensive work",
            "a valid identity against someone else's resource",
            "What a privileged identity",
            "out of logs and history other users can read",
            "covered by the repo's export and import",
            "contradicts an earlier recorded decision");
    }

    [Fact]
    public void BuilderKeepsPullRequestsHonest()
    {
        var d = Def("builder");
        ContainsAll(d,
            "# Pull request hygiene",
            "**Body matches the diff.**",
            "After every push to a PR, and before you mark it ready",
            "**Never force-update** a branch that has a PR",
            "called out in both PR bodies",
            "A PR whose head is newer than either is unchecked",
            "`Fixed in <sha>`",
            "Never leave an addressed thread without a reply",
            "A pass counts only for the head commit",
            "The fix leaves the new head unreviewed",
            "what was never run live or end to end and why");
        // The existing rules stay.
        ContainsAll(d, "Never force-push", "Never resolve a reviewer's thread for them", "never resolve a reviewer's thread for them");
    }

    [Fact]
    public void ReviewerHasTheChangeChecklist()
    {
        var d = Def("reviewer");
        ContainsAll(d,
            "**Trust boundaries.**",
            "today's only caller is honest",
            "**Checks on the final value.**",
            "**New endpoints.**",
            "**Authorization tests.**",
            "a credentialed negative test",
            "**Privileged identities.**",
            "**Egress and records.**",
            "**Portability.**",
            "**Decisions.**",
            "**Defaults.**",
            "**Widening.**",
            "**What exists.**",
            "**Never run.**",
            "the head commit reviewed",
            "reply on your earlier thread");
        Assert.True(d.IndexOf("**Trust boundaries.**", StringComparison.Ordinal) < d.IndexOf("# Reviewing a plan", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatorChecksTheHeadTheClaimsAndTheBoundaries()
    {
        ContainsAll(Def("validator"),
            "note that commit",
            "Exercise a UI change in a browser",
            "the PR body describes this diff",
            "off by default",
            "the diff widens is named and justified in the PR body",
            "a valid identity against someone else's resource (not only anonymous)",
            "Return the head commit you checked",
            "what you could not run live or end to end and why",
            "its head is still the commit you checked");
    }

    [Fact]
    public void PlansSettleDefaultsLiveRunsAndRationale()
    {
        ContainsAll(Def("researcher"),
            "off and not grandfathered unless the owner decides otherwise",
            "what is run live",
            "the repo's newest precedent",
            "the design rationale the Builder must commit alongside the code");
        ContainsAll(Def("reviewer"),
            "no step that runs the change live",
            "grandfathered without the input asking for it",
            "design rationale the code will need");
    }

    [Fact]
    public void RulesStayGeneric()
    {
        foreach (var id in new[] { "builder", "reviewer", "validator", "researcher" })
        {
            var d = Def(id);
            foreach (var word in new[] { "saved quer", "Snowflake", "Bugbot", "warehouse", "tenant" })
                Assert.DoesNotContain(word, d, StringComparison.OrdinalIgnoreCase);
        }
    }
}

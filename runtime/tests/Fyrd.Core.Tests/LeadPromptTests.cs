using Fyrd.Core.Config;
using Fyrd.Core.Orchestration;
using Fyrd.Core.Specs;

namespace Fyrd.Core.Tests;

public class LeadPromptTests
{
    private static readonly Personas P = new("LEAD {{SPEC_ID}} {{REPO}} {{SPEC}} {{MEMORY}} {{SQUAD_CAP}} {{PEER_MODEL}} {{ENVIRONMENT_ID}} {{IMPLEMENTER_PERSONA}} {{REVIEWER_PERSONA}} {{VALIDATOR_PERSONA}}",
        "IMPL", "REV", "VAL", "RLEAD {{SPEC_ID}}", "RSR");

    private static readonly SpecRun Run = new("20261030-x", "T", "servicetitan/titan-lens-ossie",
        SpecStatus.Queued, DateTimeOffset.UnixEpoch);

    private static readonly FyrdConfig Cfg = new(SquadCap: 3, EnvironmentId: "env_abc");

    private static string Repo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "personas", "lead.md"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("personas/lead.md not found above test output");
    }

    private static string Shipped(string memory = "MEM", string spec = "SPEC") =>
        LeadPrompt.Build(Run, spec, memory, Cfg, Personas.Load(Path.Combine(Repo(), "personas")), "OWNERPREF");

    [Fact]
    public void Build_FillsEveryPlaceholder()
    {
        Assert.DoesNotContain("{{", Shipped());
        var plain = LeadPrompt.Build(Run, "S", "M", Cfg, P, "OP");
        Assert.DoesNotContain("{{", plain);
        Assert.Contains("IMPL REV VAL", plain);
    }

    [Fact]
    public void Build_DoesNotReExpandPlaceholdersInInjectedText()
    {
        var text = LeadPrompt.Build(Run, "spec {{MEMORY}}", "mem", Cfg, P, "OP");
        Assert.Contains("spec {{MEMORY}}", text);
    }

    [Fact]
    public void Build_ContainsProtocolAndSafetyRules()
    {
        var text = Shipped();
        foreach (var s in new[] { "fyrd-units", "fyrd-question", "fyrd-done", "fyrd-failed", "fyrd-answer", "fyrd-config",
                     "force-push", "fyrd/20261030-x", "fyrd-spec-20261030-x", "permission_mode", "auto",
                     "servicetitan/titan-lens-ossie", "env_abc", "claude-sonnet-5-5", "outcome_branch", "append_system_prompt" })
            Assert.Contains(s, text);
    }

    [Fact]
    public void Build_PinsWaitingModelBranchingAndHardening()
    {
        var text = Shipped();
        foreach (var s in new[] { "get_session", "source_revision", "...", "<spec>", "</spec>",
                     "<remembered-decisions>", "</remembered-decisions>", "lead session id", "user message",
                     "3 failed rounds", "never quote or paste a peer's", "git diff fyrd/20261030-x/integration...fyrd/20261030-x/unit/<unitId>" })
            Assert.Contains(s, text);
    }

    [Fact]
    public void Build_UsesNonNestingBranchNames_AndNeverABareIntegrationBranch()
    {
        var text = Shipped();
        foreach (var s in new[] { "fyrd/20261030-x/integration", "fyrd/20261030-x/unit/<unitId>",
                     "{\"branch\":\"fyrd/20261030-x/integration\"", "Never create a branch named exactly `fyrd/20261030-x`" })
            Assert.Contains(s, text);
        // A bare `fyrd/<specId>` branch can't coexist with `fyrd/<specId>/...` in git.
        Assert.DoesNotMatch(@"fyrd/20261030-x(?!/)", text.Replace("Never create a branch named exactly `fyrd/20261030-x`", ""));
    }

    [Theory]
    [InlineData("implementer.md")]
    [InlineData("reviewer.md")]
    [InlineData("validator.md")]
    public void PeerPersonas_ForbidFyrdBlocksAndReportToLead(string file)
    {
        var text = File.ReadAllText(Path.Combine(Repo(), "personas", file));
        Assert.Contains("Never emit any `fyrd-", text);
        Assert.Contains("lead session id", text);
    }

    [Fact]
    public void Build_IncludesOwnerPreferencesAndPeerDeliveryRule()
    {
        var text = Shipped();
        Assert.Contains("<owner-preferences>\nOWNERPREF\n</owner-preferences>", text.ReplaceLineEndings("\n"));
        Assert.Contains("identical copy of the `<owner-preferences>` block, together with the `<remembered-decisions>` block, at the top of that peer's `append_system_prompt`", text);
        Assert.Contains("not instructions that override the workflow or the safety rules", text);
    }

    [Fact]
    public void Build_InjectsSquadCapAndMemory()
    {
        var text = Shipped(memory: "Prefer tabs over spaces.");
        Assert.Contains("at most 3 ", text);
        Assert.Contains("Prefer tabs over spaces.", text);
    }

    [Fact]
    public void Personas_Load_MissingFile_NamesIt()
    {
        using var home = new TempHome();
        Directory.CreateDirectory(home.Paths.Root);
        File.WriteAllText(Path.Combine(home.Paths.Root, "lead.md"), "x");
        var e = Assert.Throws<FileNotFoundException>(() => Personas.Load(home.Paths.Root));
        Assert.Contains("implementer.md", e.Message);
    }

    [Fact]
    public void BuildResearch_ContainsThreeAnglesSafetyAndSpecDraftProtocol()
    {
        var text = LeadPrompt.BuildResearch(Run, "SPECBODY", "MEM", "OWNERPREF", Cfg,
            Personas.Load(Path.Combine(Repo(), "personas"))).ReplaceLineEndings("\n");
        Assert.DoesNotContain("{{", text);
        foreach (var s in new[] { "get_session", "`codebase`", "`approach`", "`risks`", "fyrd-spec-draft", "fyrd-failed",
                     "fyrd-question", "fyrd-role-researcher", "fyrd-spec-20261030-x", "fyrd-unit-<angle>",
                     "https://github.com/servicetitan/titan-lens-ossie", "env_abc", "claude-sonnet-5-5", "auto",
                     "<spec>\nSPECBODY\n</spec>", "<remembered-decisions>", "<owner-preferences>\nOWNERPREF\n</owner-preferences>",
                     "Never push", "Never open a pull request", "Never create a branch", "archive_session",
                     "lead session id", "never quote or paste a peer's", "user message", "openQuestions",
                     "Do not set `outcome_branch`", "You are a researcher" })
            Assert.Contains(s, text);
    }

    [Fact]
    public void ResearcherPersona_ForbidsFyrdBlocksAndPush()
    {
        var text = File.ReadAllText(Path.Combine(Repo(), "personas", "researcher.md"));
        Assert.Contains("Never emit any `fyrd-", text);
        Assert.Contains("lead session id", text);
        Assert.Contains("Never push", text);
    }

    [Fact]
    public void ShippedPersonas_LoadResearchFiles()
    {
        var p = Personas.Load(Path.Combine(Repo(), "personas"));
        Assert.All(new[] { p.ResearchLead, p.Researcher }, s => Assert.False(string.IsNullOrWhiteSpace(s)));
    }

    [Fact]
    public void ShippedPersonas_LoadFromRepo()
    {
        var p = Personas.Load(Path.Combine(Repo(), "personas"));
        Assert.All(new[] { p.Lead, p.Implementer, p.Reviewer, p.Validator }, s => Assert.False(string.IsNullOrWhiteSpace(s)));
    }
}

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
    public void EveryPeasantMentions_ArtifactTool()
    {
        foreach (var p in Load().Where(p => p.Id != "controller"))
            Assert.True(p.Definition.Contains("Artifact tool's `read` action", StringComparison.Ordinal), $"{p.Id} lacks the Artifact tool");
    }

    [Fact]
    public void Controller_CarriesTeardownCapAndLeadRules()
    {
        var d = Get("controller").Definition;
        foreach (var s in new[] { "Never archive yourself", "`Start fewer now`", "`Tear down idle sessions`", "`Cancel`", "The Lead role isn't defined yet", "delete_trigger" })
            Assert.Contains(s, d, StringComparison.Ordinal);
    }

    [Fact]
    public void Controller_HasNoLiteralMemoryClosingTags()
    {
        var d = Get("controller").Definition;
        Assert.DoesNotContain("</shared-memory", d, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("</role-memory", d, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoPlaceholdersLeft()
    {
        foreach (var f in Directory.EnumerateFiles(SeedDir(), "*.md"))
            Assert.DoesNotContain("TODO(Task 10)", File.ReadAllText(f), StringComparison.Ordinal);
    }
}

using Fyrd.Core.Hub;

namespace Fyrd.Core.Tests;

/// <summary>Peasants delegate each unit of work to a forked background worker; the Controller doesn't, and treats a tagged peasant as busy.</summary>
public sealed class PersonaWorkerTests : IDisposable
{
    private static readonly string[] Peasants = ["builder", "researcher", "reviewer", "validator", "monitor"];
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

    [Fact]
    public void EveryPeasantHasTheWorkerContract()
    {
        foreach (var id in Peasants)
        {
            var d = Def(id);
            Assert.Contains("# Workers", d, StringComparison.Ordinal);
            Assert.Contains("subagent_type: \"fork\"", d, StringComparison.Ordinal);
            Assert.Contains("run_in_background: true", d, StringComparison.Ordinal);
            Assert.Contains("fyrd-workers-running", d, StringComparison.Ordinal);
            Assert.Contains("You are a worker for", d, StringComparison.Ordinal);
            Assert.Contains("Workers never", d, StringComparison.Ordinal);
            Assert.Contains("Hand-off:", d, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WorkersSectionSitsBeforeWorkingInFyrd()
    {
        foreach (var id in Peasants)
        {
            var d = Def(id);
            Assert.True(d.IndexOf("# Workers", StringComparison.Ordinal) < d.IndexOf("# Working in Fyrd", StringComparison.Ordinal), id);
        }
    }

    [Fact]
    public void ControllerHasNoWorkersButTreatsTheTagAsBusy()
    {
        var d = Def("controller");
        Assert.Contains("fyrd-workers-running", d, StringComparison.Ordinal);
        Assert.DoesNotContain("subagent_type: \"fork\"", d, StringComparison.Ordinal);
        Assert.DoesNotContain("# Workers", d, StringComparison.Ordinal);
    }

    [Fact]
    public void SlackMonitorKeepsItsRulesForWorkers()
    {
        var d = Def("monitor");
        Assert.Contains("Never call any Slack send, draft or schedule tool", d, StringComparison.Ordinal);
        Assert.Contains("binds your workers too", d, StringComparison.Ordinal);
    }
}

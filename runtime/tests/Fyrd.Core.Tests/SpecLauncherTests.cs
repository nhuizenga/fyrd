using Fyrd.Core.CloudSessions;
using Fyrd.Core.Config;
using Fyrd.Core.Orchestration;
using Fyrd.Core.Specs;

namespace Fyrd.Core.Tests;

public class SpecLauncherTests : IDisposable
{
    private readonly TempHome _home = new();
    private readonly SpecStore _specs;
    private readonly ConfigStore _config;
    private readonly FakeCloudSessions _cloud = new();
    private readonly SpecLauncher _launcher;

    public SpecLauncherTests()
    {
        _specs = new SpecStore(_home.Paths, TimeProvider.System);
        _config = new ConfigStore(_home.Paths);
        _config.Save(new FyrdConfig(SquadCap: 3, EnvironmentId: "env_abc"));
        var personas = new Personas("LEAD {{SPEC_ID}} {{ENVIRONMENT_ID}}", "I", "R", "V");
        _launcher = new SpecLauncher(_specs, _config, new DecisionMemory(_home.Paths), _cloud, personas);
    }

    public void Dispose() => _home.Dispose();

    private string QueuedSpec()
    {
        var id = _specs.Create("Add export", "servicetitan/titan-lens-ossie", "# spec").Id;
        Assert.True(_specs.TryTransition(id, SpecStatus.Draft, SpecStatus.Queued));
        return id;
    }

    [Fact]
    public async Task Launch_CreatesLeadWithAutoModeTagsAndRepo()
    {
        var id = QueuedSpec();
        var run = await _launcher.LaunchAsync(id, default);

        var req = Assert.Single(_cloud.Created);
        Assert.Equal("auto", req.PermissionMode);
        Assert.Contains("fyrd", req.Tags);
        Assert.Contains($"fyrd-spec-{id}", req.Tags);
        Assert.Contains("fyrd-role-lead", req.Tags);
        Assert.Equal("https://github.com/servicetitan/titan-lens-ossie", req.Source!.RepoUrl);
        Assert.Equal("claude-opus-5-5", req.Model);
        Assert.Equal("env_abc", req.EnvironmentId);
        Assert.Equal("Fyrd lead · Add export", req.Title);
        Assert.Contains(id, req.FirstMessage);
        Assert.Equal(SpecStatus.Building, run.Status);
        Assert.Equal("created-1", run.SessionId);
    }

    [Fact]
    public async Task Launch_ApiFailure_RevertsToDraft()
    {
        var id = QueuedSpec();
        _cloud.ThrowOnNextCall(new CloudSessionApiException(500, "boom"));
        await Assert.ThrowsAsync<CloudSessionApiException>(() => _launcher.LaunchAsync(id, default));
        Assert.Equal(SpecStatus.Draft, _specs.Get(id)!.Status);
    }
}

using Fyrd.Core.CloudSessions;
using Fyrd.Core.Config;
using Fyrd.Core.Hub;

namespace Fyrd.Core.Tests;

public sealed class ControllerManagerTests : IDisposable
{
    private readonly TempHome _home = new();
    private readonly string _seeds = Path.Combine(Path.GetTempPath(), "fyrd-seeds-" + Guid.NewGuid());
    private readonly FakeCloudSessions _cloud = new();

    public ControllerManagerTests()
    {
        Directory.CreateDirectory(_seeds);
        File.WriteAllText(Path.Combine(_seeds, "controller.md"), "---\nname: Controller\nmodel: claude-opus-5-5\nlocked: true\n---\nControl things.");
    }

    public void Dispose()
    {
        _home.Dispose();
        if (Directory.Exists(_seeds)) Directory.Delete(_seeds, true);
    }

    private ControllerManager Make(bool withEnv = true, HubMemory? memory = null)
    {
        var config = new ConfigStore(_home.Paths);
        if (withEnv) config.Save(new FyrdConfig(EnvironmentId: "env_abc123"));
        return new ControllerManager(_cloud, new HubPersonas(_home.Paths, _seeds), memory ?? new HubMemory(_home.Paths), config, _home.Paths);
    }

    [Fact]
    public async Task FindOrCreate_None_CreatesWithTagsAndPrompt()
    {
        var info = await Make().FindOrCreateAsync(default);
        var req = Assert.Single(_cloud.Created);
        Assert.Equal("controller", req.Title);
        Assert.Equal(["fyrd-hub", "fyrd-role-controller", "fyrd-controller"], req.Tags);
        Assert.Equal("auto", req.PermissionMode);
        Assert.Equal("claude-opus-5-5", req.Model);
        Assert.Equal("env_abc123", req.EnvironmentId);
        Assert.Contains("<hub-settings>", req.AppendSystemPrompt);
        Assert.Equal("https://claude.ai/code/created-1", info.Url);
        Assert.Equal("created-1", Make().CurrentId);
    }

    [Fact]
    public async Task FindOrCreate_StoredLive_Reuses()
    {
        await Make().FindOrCreateAsync(default);
        var again = await Make().FindOrCreateAsync(default);
        Assert.Single(_cloud.Created);
        Assert.Equal("created-1", again.SessionId);
    }

    [Fact]
    public async Task FindOrCreate_IgnoresArchived()
    {
        await Make().FindOrCreateAsync(default);
        _cloud.SetSession(FakeCloudSessions.Session("created-1", "archived"));
        _cloud.SetByTag("fyrd-controller", FakeCloudSessions.Session("created-1", "archived"));
        var info = await Make().FindOrCreateAsync(default);
        Assert.Equal(2, _cloud.Created.Count);
        Assert.Equal("created-2", info.SessionId);
    }

    [Fact]
    public async Task FindOrCreate_Concurrent_CreatesOne()
    {
        var m = Make();
        await Task.WhenAll(m.FindOrCreateAsync(default), m.FindOrCreateAsync(default));
        Assert.Single(_cloud.Created);
    }

    [Fact]
    public async Task FindOrCreate_TwoTagged_ArchivesExtra()
    {
        var t = DateTimeOffset.UtcNow;
        _cloud.SetByTag("fyrd-controller",
            FakeCloudSessions.Session("old", "idle", createdAt: t.AddHours(-2)),
            FakeCloudSessions.Session("new", "idle", createdAt: t),
            FakeCloudSessions.Session("gone", "archived", createdAt: t.AddHours(1)));
        var info = await Make().FindOrCreateAsync(default);
        Assert.Equal("new", info.SessionId);
        Assert.Equal(["old"], _cloud.Archived);
        Assert.Empty(_cloud.Created);
    }

    [Fact]
    public async Task Recreate_ArchivesThenCreates()
    {
        await Make().FindOrCreateAsync(default);
        var info = await Make().RecreateAsync(default);
        Assert.Equal(["created-1"], _cloud.Archived);
        Assert.Equal("created-2", info.SessionId);
    }

    [Fact]
    public async Task FindOrCreate_NoEnvironment_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Make(withEnv: false).FindOrCreateAsync(default));
        Assert.Equal("Set a cloud environment in Settings first.", ex.Message);
    }

    [Fact]
    public async Task FindOrCreate_StoredTransientError_Propagates()
    {
        await Make().FindOrCreateAsync(default);
        _cloud.ThrowOnNextCall(new CloudSessionApiException(500, "boom"));
        await Assert.ThrowsAsync<CloudSessionApiException>(() => Make().FindOrCreateAsync(default));
        Assert.Single(_cloud.Created);
    }

    [Fact]
    public async Task PostedMemoryVersion_PersistsAndSetOnCreate()
    {
        var mem = new HubMemory(_home.Paths);
        mem.Add("shared", "x", "t", DateTimeOffset.UtcNow);
        var m = Make(memory: mem);
        await m.FindOrCreateAsync(default);
        Assert.True(mem.Version > 0);
        Assert.Equal(mem.Version, m.PostedMemoryVersion);
        m.PostedMemoryVersion = 99;
        Assert.Equal(99, Make().PostedMemoryVersion);
    }
}

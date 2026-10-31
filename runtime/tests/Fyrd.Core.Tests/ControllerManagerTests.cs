using Fyrd.Core.CloudSessions;
using Fyrd.Core.Config;
using Fyrd.Core.Hub;

namespace Fyrd.Core.Tests;

public sealed class ControllerManagerTests : IDisposable
{
    private const string Ctl = "session_ctl1";
    private const string FirstLine =
        "You are the Fyrd Controller. Follow the instructions below for the rest of this session; they replace any earlier instructions.";
    private const string Unavailable = "That session isn't available. Create a new one on claude.ai and paste its id.";

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

    private async Task Adopted(string id = Ctl)
    {
        _cloud.SetSession(FakeCloudSessions.Session(id));
        await Make().AdoptAsync(id, default);
    }

    [Fact]
    public async Task Adopt_StoresIdSendsPromptSetsHash()
    {
        var mem = new HubMemory(_home.Paths);
        mem.Add("shared", "Owner likes tea.", "t", DateTimeOffset.UtcNow);
        _cloud.SetSession(FakeCloudSessions.Session(Ctl));
        var m = Make(memory: mem);

        var info = await m.AdoptAsync(Ctl, default);

        Assert.Equal(Ctl, info.SessionId);
        Assert.Equal("https://claude.ai/code/" + Ctl, info.Url);
        Assert.Equal(Ctl, Make().CurrentId);
        var sent = Assert.Single(_cloud.Sent);
        Assert.Equal(Ctl, sent.SessionId);
        Assert.StartsWith(FirstLine + "\n\nControl things.", sent.Text);
        Assert.Contains("<hub-settings>", sent.Text);
        Assert.Contains("environment_id: env_abc123", sent.Text);
        Assert.Contains("Owner likes tea.", sent.Text);
        Assert.Empty(_cloud.Created);
        var expected = ControllerManager.MemoryHash(mem, new HubPersonas(_home.Paths, _seeds).List());
        Assert.Equal(expected, m.PostedMemoryHash);
        Assert.Equal(expected, Make().PostedMemoryHash);
    }

    [Fact]
    public async Task Adopt_NoEnvironment_StillAdopts()
    {
        _cloud.SetSession(FakeCloudSessions.Session(Ctl));
        await Make(withEnv: false).AdoptAsync(Ctl, default);
        Assert.Equal(Ctl, Make(withEnv: false).CurrentId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("created-1")]
    [InlineData("cse_abc")]
    [InlineData("session_")]
    [InlineData("session_ab/../x")]
    public async Task Adopt_BadFormat_Throws(string id)
    {
        var ex = await Assert.ThrowsAsync<HubConfigException>(() => Make().AdoptAsync(id, default));
        Assert.Contains("session_", ex.Message);
        Assert.Empty(_cloud.Sent);
        Assert.Null(Make().CurrentId);
    }

    [Fact]
    public async Task Adopt_TrimsWhitespace()
    {
        _cloud.SetSession(FakeCloudSessions.Session(Ctl));
        var info = await Make().AdoptAsync("  " + Ctl + "\n", default);
        Assert.Equal(Ctl, info.SessionId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Adopt_ArchivedOrMissing_Throws(bool archived)
    {
        if (archived) _cloud.SetSession(FakeCloudSessions.Session(Ctl, "archived"));
        var ex = await Assert.ThrowsAsync<HubConfigException>(() => Make().AdoptAsync(Ctl, default));
        Assert.Equal(Unavailable, ex.Message);
        Assert.Empty(_cloud.Sent);
        Assert.Null(Make().CurrentId);
    }

    [Fact]
    public async Task Adopt_ReplacesAndArchivesPreviousLive()
    {
        await Adopted("session_old");
        _cloud.SetSession(FakeCloudSessions.Session(Ctl));

        await Make().AdoptAsync(Ctl, default);

        Assert.Equal(["session_old"], _cloud.Archived);
        Assert.Equal(Ctl, Make().CurrentId);
        Assert.Equal(Ctl, _cloud.Sent[^1].SessionId);
    }

    [Fact]
    public async Task Adopt_PreviousArchived_NotArchivedAgain()
    {
        await Adopted("session_old");
        _cloud.SetSession(FakeCloudSessions.Session("session_old", "archived"));
        _cloud.SetSession(FakeCloudSessions.Session(Ctl));

        await Make().AdoptAsync(Ctl, default);

        Assert.Empty(_cloud.Archived);
        Assert.Equal(Ctl, Make().CurrentId);
    }

    [Fact]
    public async Task Adopt_SameId_ResendsPromptWithoutArchiving()
    {
        await Adopted();
        await Make().AdoptAsync(Ctl, default);
        Assert.Empty(_cloud.Archived);
        Assert.Equal(2, _cloud.Sent.Count);
    }

    [Fact]
    public async Task Adopt_PersonaMissing_ThrowsBeforeAnySideEffect()
    {
        await Adopted("session_old");
        _cloud.SetSession(FakeCloudSessions.Session(Ctl));
        File.WriteAllText(Path.Combine(_home.Paths.HubPersonasDir, "controller.json"), "{ not json");

        var ex = await Assert.ThrowsAsync<HubConfigException>(() => Make().AdoptAsync(Ctl, default));

        Assert.Equal("The Controller persona is missing.", ex.Message);
        Assert.Empty(_cloud.Archived);
        Assert.Single(_cloud.Sent);
        Assert.Equal("session_old", Make().CurrentId);
    }

    [Fact]
    public async Task Adopt_Concurrent_OneController()
    {
        _cloud.SetSession(FakeCloudSessions.Session("session_a"));
        _cloud.SetSession(FakeCloudSessions.Session("session_b"));
        var m = Make();
        await Task.WhenAll(m.AdoptAsync("session_a", default), m.AdoptAsync("session_b", default));
        // Serialised: the second adoption archives the first.
        var archived = Assert.Single(_cloud.Archived);
        Assert.NotEqual(archived, m.CurrentId);
    }

    [Fact]
    public async Task Find_Live_ReturnsInfo()
    {
        await Adopted();
        var info = await Make().FindAsync(default);
        Assert.Equal(Ctl, info?.SessionId);
        Assert.Equal("https://claude.ai/code/" + Ctl, info?.Url);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Find_ArchivedOrGone_ClearsAndReturnsNull(bool archived)
    {
        await Adopted();
        if (archived) _cloud.SetSession(FakeCloudSessions.Session(Ctl, "archived"));
        else _cloud.ThrowOnNextCall(new CloudSessionApiException(404, "gone"));

        Assert.Null(await Make().FindAsync(default));
        Assert.Null(Make().CurrentId);
        Assert.Null(Make().PostedMemoryHash);
    }

    [Fact]
    public async Task Find_None_ReturnsNullWithoutCalls()
    {
        _cloud.ThrowOnNextCall(new CloudSessionApiException(500, "should not be called"));
        Assert.Null(await Make().FindAsync(default));
    }

    [Fact]
    public async Task Find_TransientError_PropagatesAndKeepsId()
    {
        await Adopted();
        _cloud.ThrowOnNextCall(new CloudSessionApiException(500, "boom"));
        await Assert.ThrowsAsync<CloudSessionApiException>(() => Make().FindAsync(default));
        Assert.Equal(Ctl, Make().CurrentId);
    }

    [Fact]
    public async Task Retire_ArchivesAndClears()
    {
        await Adopted();
        var m = Make();
        await m.RetireAsync(default);
        Assert.Equal([Ctl], _cloud.Archived);
        Assert.Null(m.CurrentId);
        Assert.Null(Make().CurrentId);
        Assert.Null(Make().PostedMemoryHash);
        Assert.Empty(_cloud.Created);
    }

    [Fact]
    public async Task Retire_Archive404_StillClears()
    {
        await Adopted();
        _cloud.ThrowOnNextCall(new CloudSessionApiException(404, "gone"));
        await Make().RetireAsync(default);
        Assert.Null(Make().CurrentId);
    }

    [Fact]
    public async Task Retire_TransientError_PropagatesAndKeepsId()
    {
        await Adopted();
        _cloud.ThrowOnNextCall(new CloudSessionApiException(500, "boom"));
        await Assert.ThrowsAsync<CloudSessionApiException>(() => Make().RetireAsync(default));
        Assert.Equal(Ctl, Make().CurrentId);
    }

    [Fact]
    public async Task Retire_None_NoCalls()
    {
        await Make().RetireAsync(default);
        Assert.Empty(_cloud.Archived);
    }
}

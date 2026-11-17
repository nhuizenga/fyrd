using Fyrd.Core.CloudSessions;
using Fyrd.Core.Config;
using Fyrd.Core.Hub;

namespace Fyrd.Core.Tests;

public sealed class ControllerManagerTests : IDisposable
{
    private const string Ctl = "session_ctl1";
    private const string FirstLine =
        "You are the Fyrd Controller. I started this session for that role (its first message, \"controller\", only " +
        "named it), so please follow the instructions below.";

    private readonly TempHome _home = new();
    private readonly string _seeds = Path.Combine(Path.GetTempPath(), "fyrd-seeds-" + Guid.NewGuid());
    private readonly FakeCloudSessions _cloud = new();
    private readonly FakeOwnerMessages _owner = new();

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
        return new ControllerManager(_cloud, _owner, new HubPersonas(_home.Paths, _seeds), memory ?? new HubMemory(_home.Paths), config, _home.Paths);
    }

    /// <summary>Creates a Controller whose new session gets <paramref name="id"/> (and is live in the fake cloud).</summary>
    private async Task Created(string id = Ctl)
    {
        _cloud.SetSession(FakeCloudSessions.Session(id));
        _owner.CreatedIds.Enqueue(id);
        await Make().CreateAsync(default);
    }

    [Fact]
    public async Task Create_CreatesSessionWithWholePrompt_StoresIdSetsHash()
    {
        var mem = new HubMemory(_home.Paths);
        mem.Add("shared", "Owner likes tea.", "t", DateTimeOffset.UtcNow);
        _owner.CreatedIds.Enqueue(Ctl);
        var m = Make(memory: mem);

        var info = await m.CreateAsync(default);

        Assert.Equal(Ctl, info.SessionId);
        Assert.Equal("https://claude.ai/code/" + Ctl, info.Url);
        Assert.Equal(Ctl, Make().CurrentId);
        var created = Assert.Single(_owner.Created);
        Assert.Equal(Ctl, created.SessionId);
        Assert.Equal("controller", created.Description);
        Assert.Equal(Path.Combine(_home.Paths.HubDir, "controller-launch"), created.WorkingDirectory);
        Assert.Equal(m.LaunchDirectory, created.WorkingDirectory);
        // The whole opening prompt follows as one owner turn; nothing goes through the cloud API.
        var sent = Assert.Single(_owner.Sent);
        Assert.Equal(Ctl, sent.SessionId);
        Assert.Equal(m.BuildPrompt().Prompt, sent.Text);
        Assert.StartsWith(FirstLine + "\n\nControl things.", sent.Text);
        Assert.Contains("<hub-settings>", sent.Text);
        Assert.Contains("environment_id: env_abc123", sent.Text);
        Assert.Contains("Owner likes tea.", sent.Text);
        Assert.Empty(_cloud.Created);
        Assert.Null(m.PendingId);
        var expected = ControllerManager.MemoryHash(mem, new HubPersonas(_home.Paths, _seeds).List());
        Assert.Equal(expected, m.PostedMemoryHash);
        Assert.Equal(expected, Make().PostedMemoryHash);
        Assert.Equal(m.InstructionsHash(), Make().PostedInstructionsHash);
    }

    [Fact]
    public async Task Create_CliFails_PropagatesAndKeepsCurrentController()
    {
        await Created("session_old");
        _owner.ThrowOnNextCall(new OwnerMessageException("claude is not on PATH"));

        await Assert.ThrowsAsync<OwnerMessageException>(() => Make().CreateAsync(default));

        Assert.Equal("session_old", Make().CurrentId);
        Assert.Empty(_cloud.Archived);
    }

    [Fact]
    public async Task Create_PromptNotDelivered_KeepsSessionPendingAndCurrentController()
    {
        await Created("session_old");
        _cloud.SetSession(FakeCloudSessions.Session(Ctl));
        _owner.CreatedIds.Enqueue(Ctl);
        // The call after the create is the prompt's send.
        _owner.OnCreate = _ => { _owner.ThrowOnNextCall(new OwnerMessageException("claude timed out")); return Task.CompletedTask; };

        var e = await Assert.ThrowsAsync<OwnerMessageException>(() => Make().CreateAsync(default));

        Assert.Contains(Ctl, e.Message);
        Assert.Contains("wasn't delivered: claude timed out", e.Message);
        Assert.Equal("session_old", Make().CurrentId);
        Assert.Equal(Ctl, Make().PendingId);
        Assert.Empty(_cloud.Archived);
    }

    [Fact]
    public async Task Create_AfterPromptNotDelivered_ResendsToTheSameSession()
    {
        await Created("session_old");
        _cloud.SetSession(FakeCloudSessions.Session(Ctl));
        _owner.CreatedIds.Enqueue(Ctl);
        _owner.OnCreate = _ => { _owner.ThrowOnNextCall(new OwnerMessageException("claude timed out")); return Task.CompletedTask; };
        await Assert.ThrowsAsync<OwnerMessageException>(() => Make().CreateAsync(default));
        _owner.OnCreate = null;
        var m = Make();

        var info = await m.CreateAsync(default);

        Assert.Equal(Ctl, info.SessionId);
        Assert.Equal(2, _owner.Created.Count); // the old Controller's and this one's; the retry created nothing
        Assert.Equal((Ctl, m.BuildPrompt().Prompt), _owner.Sent[^1]);
        Assert.Equal(Ctl, Make().CurrentId);
        Assert.Null(Make().PendingId);
        Assert.Equal(["session_old"], _cloud.Archived);
    }

    [Fact]
    public async Task Create_PendingSessionGone_CreatesANewOne()
    {
        _owner.CreatedIds.Enqueue("session_lost");
        _owner.OnCreate = _ => { _owner.ThrowOnNextCall(new OwnerMessageException("claude timed out")); return Task.CompletedTask; };
        await Assert.ThrowsAsync<OwnerMessageException>(() => Make().CreateAsync(default));
        _owner.OnCreate = null;
        _cloud.SetSession(FakeCloudSessions.Session("session_lost", "archived"));
        _owner.CreatedIds.Enqueue(Ctl);

        var info = await Make().CreateAsync(default);

        Assert.Equal(Ctl, info.SessionId);
        Assert.Equal(["session_lost", Ctl], _owner.Created.Select(c => c.SessionId));
        Assert.Null(Make().PendingId);
    }

    [Fact]
    public async Task Find_And_Retire_KeepAPendingSession()
    {
        _cloud.SetSession(FakeCloudSessions.Session(Ctl));
        _owner.CreatedIds.Enqueue(Ctl);
        _owner.OnCreate = _ => { _owner.ThrowOnNextCall(new OwnerMessageException("x")); return Task.CompletedTask; };
        await Assert.ThrowsAsync<OwnerMessageException>(() => Make().CreateAsync(default));

        Assert.Null(await Make().FindAsync(default));
        await Make().RetireAsync(default);

        Assert.Equal(Ctl, Make().PendingId);
    }

    [Fact]
    public async Task Create_CliFails_NoController_StoresNothing()
    {
        _owner.Fail = new OwnerMessageException("claude is not on PATH");
        await Assert.ThrowsAsync<OwnerMessageException>(() => Make().CreateAsync(default));
        Assert.Null(Make().CurrentId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("cse_abc")]
    [InlineData("session_ab/../x")]
    public async Task Create_UnexpectedId_ThrowsAndStoresNothing(string id)
    {
        _owner.CreatedIds.Enqueue(id);
        await Assert.ThrowsAsync<HubConfigException>(() => Make().CreateAsync(default));
        Assert.Null(Make().CurrentId);
        Assert.Null(Make().PendingId);
        Assert.Empty(_owner.Sent);
    }

    [Fact]
    public async Task Create_NoEnvironment_StillCreates()
    {
        _owner.CreatedIds.Enqueue(Ctl);
        await Make(withEnv: false).CreateAsync(default);
        Assert.Equal(Ctl, Make(withEnv: false).CurrentId);
    }

    [Fact]
    public async Task Create_SetsCreatedAt()
    {
        var at = DateTimeOffset.Parse("2026-10-31T12:00:00Z");
        var config = new ConfigStore(_home.Paths);
        var m = new ControllerManager(_cloud, _owner, new HubPersonas(_home.Paths, _seeds), new HubMemory(_home.Paths), config, _home.Paths, new FixedTime(at));
        Assert.Null(m.CreatedAt);
        await m.CreateAsync(default);
        Assert.Equal(at, m.CreatedAt);
    }

    [Fact]
    public async Task Create_ReplacesAndArchivesPreviousLive()
    {
        await Created("session_old");

        await Created(Ctl);

        Assert.Equal(["session_old"], _cloud.Archived);
        Assert.Equal(Ctl, Make().CurrentId);
        Assert.Equal(Ctl, _owner.Sent[^1].SessionId);
    }

    [Fact]
    public async Task Create_PreviousArchivedOrGone_NotArchivedAgain()
    {
        await Created("session_old");
        _cloud.SetSession(FakeCloudSessions.Session("session_old", "archived"));

        await Created(Ctl);

        Assert.Empty(_cloud.Archived);
        Assert.Equal(Ctl, Make().CurrentId);
    }

    [Fact]
    public async Task Create_PersonaMissing_ThrowsBeforeAnySideEffect()
    {
        await Created("session_old");
        File.WriteAllText(Path.Combine(_home.Paths.HubPersonasDir, "controller.json"), "{ not json");

        var ex = await Assert.ThrowsAsync<HubConfigException>(() => Make().CreateAsync(default));

        Assert.Equal("The Controller persona is missing.", ex.Message);
        Assert.Empty(_cloud.Archived);
        Assert.Single(_owner.Created);
        Assert.Equal("session_old", Make().CurrentId);
    }

    [Fact]
    public async Task Create_WhileAnotherRuns_FailsFastWithoutCallingCli()
    {
        var hold = new TaskCompletionSource();
        _owner.OnCreate = _ => hold.Task;
        var m = Make();
        var first = m.CreateAsync(default);

        await Assert.ThrowsAsync<ControllerBusyException>(() => m.CreateAsync(default));

        hold.SetResult();
        Assert.Equal("session_ctl", (await first).SessionId);
        Assert.Single(_owner.Created);
    }

    [Fact]
    public async Task Create_WhileCliRuns_FindAndRetireDoNotWait()
    {
        await Created();
        var hold = new TaskCompletionSource();
        _owner.OnCreate = _ => hold.Task;
        _owner.CreatedIds.Enqueue("session_new");
        var m = Make();
        var create = m.CreateAsync(default);

        var found = m.FindAsync(default);
        Assert.True(found.IsCompleted, "Find waited for the CLI.");
        Assert.Equal(Ctl, (await found)?.SessionId);

        hold.SetResult();
        await create;
        Assert.Equal("session_new", m.CurrentId);
    }

    [Fact]
    public async Task Create_PreviousCheckFails_StoresNewIdAndWarns()
    {
        await Created("session_old");
        _cloud.ThrowOnGetFor("session_old", new CloudSessionApiException(500, "boom"));
        _owner.CreatedIds.Enqueue(Ctl);

        var info = await Make().CreateAsync(default);

        Assert.Equal(Ctl, info.SessionId);
        Assert.Contains("session_old", info.Warning);
        Assert.Contains("could not be archived", info.Warning);
        Assert.Equal(Ctl, Make().CurrentId);
        Assert.NotNull(Make().PostedMemoryHash);
        Assert.Empty(_cloud.Archived);
    }

    [Fact]
    public async Task Create_PreviousArchiveFails_StoresNewIdAndWarns()
    {
        await Created("session_old");
        _owner.CreatedIds.Enqueue(Ctl);
        _owner.OnCreate = _ =>
        {
            // The live check succeeds; the archive after it fails.
            _cloud.SkipCalls(1);
            _cloud.ThrowOnNextCall(new HttpRequestException("network down"));
            return Task.CompletedTask;
        };

        var info = await Make().CreateAsync(default);

        Assert.Equal(Ctl, info.SessionId);
        Assert.Contains("network down", info.Warning);
        Assert.Equal(Ctl, Make().CurrentId);
    }

    [Fact]
    public async Task Create_PreviousArchived_NoWarning()
    {
        await Created("session_old");
        await Created(Ctl);
        _owner.CreatedIds.Enqueue("session_third");
        var info = await Make().CreateAsync(default);
        Assert.Null(info.Warning);
    }

    [Fact]
    public async Task Find_Live_ReturnsInfo()
    {
        await Created();
        var info = await Make().FindAsync(default);
        Assert.Equal(Ctl, info?.SessionId);
        Assert.Equal("https://claude.ai/code/" + Ctl, info?.Url);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Find_ArchivedOrGone_ClearsAndReturnsNull(bool archived)
    {
        await Created();
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
        await Created();
        _cloud.ThrowOnNextCall(new CloudSessionApiException(500, "boom"));
        await Assert.ThrowsAsync<CloudSessionApiException>(() => Make().FindAsync(default));
        Assert.Equal(Ctl, Make().CurrentId);
    }

    [Fact]
    public async Task Retire_ArchivesAndClears()
    {
        await Created();
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
        await Created();
        _cloud.ThrowOnNextCall(new CloudSessionApiException(404, "gone"));
        await Make().RetireAsync(default);
        Assert.Null(Make().CurrentId);
    }

    [Fact]
    public async Task Retire_TransientError_PropagatesAndKeepsId()
    {
        await Created();
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

using Fyrd.Core.Hub;

namespace Fyrd.Core.Tests;

public sealed class HubPersonasTests : IDisposable
{
    private readonly TempHome _home = new();
    private readonly string _seeds = Path.Combine(Path.GetTempPath(), "fyrd-seeds-" + Guid.NewGuid());

    public HubPersonasTests()
    {
        Directory.CreateDirectory(_seeds);
        PersonaSeedFile.Write(_seeds, "builder", "Builder", "claude-sonnet-5-5", false, "Build things.");
        PersonaSeedFile.Write(_seeds, "controller", "Controller", "claude-sonnet-5-5", true, "Control things.");
    }

    public void Dispose()
    {
        _home.Dispose();
        if (Directory.Exists(_seeds)) Directory.Delete(_seeds, true);
    }

    private HubPersonas Make() => new(_home.Paths, _seeds);

    [Fact]
    public void List_SeedsOnce_ControllerFirst()
    {
        var store = Make();
        var list = store.List();
        Assert.Equal("controller", list[0].Id);
        Assert.True(list[0].Locked);
        Assert.Equal("Control things.", list[0].Definition);
        var builder = list.Single(p => p.Id == "builder");
        store.Save(builder with { Definition = "Edited" });
        Assert.Equal("Edited", Make().List().Single(p => p.Id == "builder").Definition);
    }

    private void Seed(string id, string name, string body, bool locked = false) =>
        PersonaSeedFile.Write(_seeds, id, name, "claude-sonnet-5-5", locked, body);

    [Fact]
    public void Hash_IsSha256OfNameModelDefinition() =>
        Assert.Equal("22319754cb8179c5f39718c645c51fbf82767867aedf0db954c8d94497577495", HubPersonas.Hash("A", "m", "d"));

    [Fact]
    public void UneditedPersona_UpdatesToNewSeed_AndSeedHashAdvances()
    {
        var first = Make().Get("builder")!;
        Assert.Equal(HubPersonas.Hash(first), first.SeedHash);
        Seed("builder", "Builder", "Build better things.");
        var after = Make().Get("builder")!;
        Assert.Equal("Build better things.", after.Definition);
        Assert.Equal(HubPersonas.Hash(after), after.SeedHash);
        Assert.False(after.UpdateAvailable);
    }

    [Fact]
    public void EditedPersona_IsLeftAlone_AndFlagged_ThenResetClearsIt()
    {
        var store = Make();
        store.Save(store.Get("builder")! with { Definition = "Mine" });
        Seed("builder", "Builder", "Build better things.");
        var again = Make();
        var p = again.Get("builder")!;
        Assert.Equal("Mine", p.Definition);
        Assert.True(p.UpdateAvailable);
        var reset = again.Reset("builder");
        Assert.Equal("Build better things.", reset.Definition);
        Assert.False(again.Get("builder")!.UpdateAvailable);
    }

    [Fact]
    public void EditedPersona_WithUnchangedSeed_IsNotFlagged()
    {
        var store = Make();
        store.Save(store.Get("builder")! with { Definition = "Mine" });
        Assert.False(Make().Get("builder")!.UpdateAvailable);
    }

    [Fact]
    public void LegacyPersona_InHistory_IsUpdated_NotInHistory_IsFlagged()
    {
        Directory.CreateDirectory(_home.Paths.HubPersonasDir);
        var oldBuilder = new HubPersona("builder", "Builder", "claude-sonnet-5-5", "Old text.", false);
        var oldScout = new HubPersona("scout", "Scout", "claude-sonnet-5-5", "Custom scout.", false);
        foreach (var p in new[] { oldBuilder, oldScout })
            File.WriteAllText(Path.Combine(_home.Paths.HubPersonasDir, p.Id + ".json"), System.Text.Json.JsonSerializer.Serialize(p, Json.Options));
        File.WriteAllText(Path.Combine(_home.Paths.HubPersonasDir, ".seeded"), "builder\ncontroller\nscout\n");
        Seed("scout", "Scout", "New scout.");
        File.WriteAllText(Path.Combine(_seeds, "persona-seed-history.json"), $"{{\"builder\":[\"{HubPersonas.Hash(oldBuilder)}\"]}}");
        var store = Make();
        Assert.Equal("Build things.", store.Get("builder")!.Definition);
        var scout = store.Get("scout")!;
        Assert.Equal("Custom scout.", scout.Definition);
        Assert.True(scout.UpdateAvailable);
    }

    [Fact]
    public void CrlfSeed_HashesLikeLf()
    {
        Make().List();
        File.WriteAllText(Path.Combine(_seeds, "builder", "SKILL.md"), PersonaSeedFile.Text("builder", "Builder", "claude-sonnet-5-5", false, "Build\r\nthings.", "\r\n"));
        var p = Make().Get("builder")!;
        Assert.Equal("Build\nthings.", p.Definition);
    }

    [Fact]
    public void LockedPersona_WithDifferentStoredName_StillUpdates()
    {
        Make().List();
        var file = Path.Combine(_home.Paths.HubPersonasDir, "controller.json");
        var stored = System.Text.Json.JsonSerializer.Deserialize<HubPersona>(File.ReadAllText(file), Json.Options)!;
        var renamed = stored with { Name = "Boss", SeedHash = null };
        renamed = renamed with { SeedHash = HubPersonas.Hash(renamed) };
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(renamed, Json.Options));
        Seed("controller", "Controller", "Control better.", locked: true);
        var c = Make().Get("controller")!;
        Assert.Equal("Boss", c.Name);
        Assert.Equal("Control better.", c.Definition);
        Assert.False(c.UpdateAvailable);
    }

    [Fact]
    public void DeletedPersona_StaysDeleted()
    {
        var store = Make();
        store.List();
        store.Delete("builder");
        Assert.Null(Make().Get("builder"));
    }

    [Fact]
    public void LockedPersona_KeepsItsName_WhenUpdated()
    {
        Make().List();
        Seed("controller", "Controller", "Control better.", locked: true);
        var c = Make().Get("controller")!;
        Assert.Equal("Controller", c.Name);
        Assert.Equal("Control better.", c.Definition);
        Assert.True(c.Locked);
    }

    [Fact]
    public void Reset_WithoutSeed_Throws()
    {
        var store = Make();
        store.Save(new HubPersona("", "Scout", "claude-haiku-5-5", "d", false));
        Assert.Throws<KeyNotFoundException>(() => store.Reset("scout"));
    }

    [Fact]
    public void Delete_Locked_Throws()
    {
        var store = Make();
        store.List();
        Assert.Throws<InvalidOperationException>(() => store.Delete("controller"));
    }

    [Fact]
    public void Delete_Unknown_IsNoOp() => Make().Delete("nope");

    [Fact]
    public void Save_UnknownModel_Throws() =>
        Assert.Throws<ArgumentException>(() => Make().Save(new HubPersona("", "X", "gpt-4", "d", false)));

    [Fact]
    public void Save_New_IdIsSlug()
    {
        var saved = Make().Save(new HubPersona("", "Data Engineer", "claude-haiku-5-5", "d", false));
        Assert.Equal("data-engineer", saved.Id);
        Assert.Equal("d", Make().Get("data-engineer")!.Definition);
    }

    [Fact]
    public void Save_New_SlugCollision_Throws()
    {
        var store = Make();
        store.Save(new HubPersona("", "Data Engineer", "claude-haiku-5-5", "d", false));
        Assert.Throws<InvalidOperationException>(() => store.Save(new HubPersona("", "data engineer", "claude-haiku-5-5", "d", false)));
    }

    [Fact]
    public void Save_Existing_KeepsIdAndLocked()
    {
        var store = Make();
        store.List();
        var saved = store.Save(new HubPersona("controller", "Controller", "claude-opus-5-5", "x", false));
        Assert.Equal("controller", saved.Id);
        Assert.True(saved.Locked);
        Assert.Equal("claude-opus-5-5", store.Get("controller")!.Model);
    }

    [Fact]
    public void Save_Existing_RenamesUnlockedPersona_KeepsId()
    {
        var store = Make();
        store.List();
        var saved = store.Save(new HubPersona("builder", "Smith", "claude-sonnet-5-5", "x", false));
        Assert.Equal("builder", saved.Id);
        Assert.Equal("Smith", store.Get("builder")!.Name);
    }

    [Fact]
    public void Save_Existing_RenamingLockedPersona_Throws()
    {
        var store = Make();
        store.List();
        Assert.Throws<InvalidOperationException>(() =>
            store.Save(new HubPersona("controller", "Boss", "claude-sonnet-5-5", "x", false)));
        Assert.Equal("Controller", store.Get("controller")!.Name);
    }

    [Fact]
    public void Delete_Seeded_StaysDeletedAfterRestart()
    {
        var store = Make();
        store.List();
        store.Delete("builder");
        var again = Make();
        Assert.Null(again.Get("builder"));
        Assert.DoesNotContain(again.List(), p => p.Id == "builder");
        Assert.Contains(again.List(), p => p.Id == "controller");
    }

    [Theory]
    [InlineData("", "Shared")]
    [InlineData("shared", "Whatever")]
    public void Save_ReservedSharedId_Throws(string id, string name) =>
        Assert.Throws<ArgumentException>(() => Make().Save(new HubPersona(id, name, "claude-haiku-5-5", "d", false)));
}

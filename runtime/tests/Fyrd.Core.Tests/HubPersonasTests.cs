using System.Text.Json;
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

    private void Seed(string id, string name, string body, bool locked = false, string model = "claude-sonnet-5-5") =>
        PersonaSeedFile.Write(_seeds, id, name, model, locked, body);

    private string FileFor(string id) => Path.Combine(_home.Paths.HubPersonasDir, id + ".json");

    /// <summary>Writes a whole-persona file the way installs before model-only overrides stored one.</summary>
    private void WriteLegacy(string id, string name, string model, string definition, string? seedHash)
    {
        Directory.CreateDirectory(_home.Paths.HubPersonasDir);
        File.WriteAllText(FileFor(id), JsonSerializer.Serialize(
            new { id, name, model, definition, locked = false, seedHash }, Json.Options));
    }

    [Fact]
    public void List_IsTheSeeds_ControllerFirst()
    {
        var list = Make().List();
        Assert.Equal(["controller", "builder"], list.Select(p => p.Id));
        Assert.True(list[0].Locked);
        Assert.Equal("Control things.", list[0].Definition);
        Assert.Equal("claude-sonnet-5-5", list[1].DefaultModel);
    }

    [Fact]
    public void Hash_IsSha256OfNameModelDefinition() =>
        Assert.Equal("22319754cb8179c5f39718c645c51fbf82767867aedf0db954c8d94497577495", HubPersonas.Hash("A", "m", "d"));

    [Fact]
    public void SetModel_IsKept_AndThePersonaStillFollowsTheRepoText()
    {
        var saved = Make().SetModel("builder", "claude-opus-5-5");
        Assert.Equal("claude-opus-5-5", saved.Model);
        Assert.Equal("claude-sonnet-5-5", saved.DefaultModel);
        Seed("builder", "Smith", "Build better things.");
        var after = Make().Get("builder")!;
        Assert.Equal("claude-opus-5-5", after.Model);
        Assert.Equal("Smith", after.Name);
        Assert.Equal("Build better things.", after.Definition);
    }

    [Fact]
    public void SetModel_ToTheDefault_ClearsTheOverride_SoTheRepoModelIsFollowed()
    {
        var store = Make();
        store.SetModel("builder", "claude-opus-5-5");
        store.SetModel("builder", "claude-sonnet-5-5");
        Assert.False(File.Exists(FileFor("builder")));
        Seed("builder", "Builder", "Build things.", model: "claude-haiku-5-5");
        Assert.Equal("claude-haiku-5-5", Make().Get("builder")!.Model);
    }

    [Fact]
    public void Reset_ClearsTheOverride()
    {
        var store = Make();
        store.SetModel("builder", "claude-opus-5-5");
        var reset = store.Reset("builder");
        Assert.Equal("claude-sonnet-5-5", reset.Model);
        Assert.Equal("claude-sonnet-5-5", Make().Get("builder")!.Model);
    }

    [Fact]
    public void SetModel_And_Reset_OfAnUnknownPersona_Throw()
    {
        Assert.Throws<KeyNotFoundException>(() => Make().SetModel("scout", "claude-opus-5-5"));
        Assert.Throws<KeyNotFoundException>(() => Make().Reset("scout"));
        Assert.Throws<ArgumentException>(() => Make().Reset("Bad_Id"));
    }

    [Fact]
    public void SetModel_UnknownModel_Throws() =>
        Assert.Throws<ArgumentException>(() => Make().SetModel("builder", "gpt-4"));

    [Fact]
    public void ControllerModel_CanChange()
    {
        var saved = Make().SetModel("controller", "claude-opus-5-5");
        Assert.True(saved.Locked);
        Assert.Equal("Controller", saved.Name);
        Assert.Equal("claude-opus-5-5", Make().Get("controller")!.Model);
    }

    [Fact]
    public void LegacyEditedPersona_KeepsItsModel_ButTakesTheRepoNameAndText()
    {
        WriteLegacy("builder", "Smith", "claude-opus-5-5", "My own text.", seedHash: "0000");
        var p = Make().Get("builder")!;
        Assert.Equal("claude-opus-5-5", p.Model);
        Assert.Equal("Builder", p.Name);
        Assert.Equal("Build things.", p.Definition);
    }

    [Fact]
    public void LegacyUneditedPersona_HasNoOverride()
    {
        // Synced to an older seed whose model was opus: it followed its seed, so it takes today's seed model.
        WriteLegacy("builder", "Builder", "claude-opus-5-5", "Old text.", HubPersonas.Hash("Builder", "claude-opus-5-5", "Old text."));
        Assert.Equal("claude-sonnet-5-5", Make().Get("builder")!.Model);
    }

    [Fact]
    public void LegacyPersona_InHistory_HasNoOverride()
    {
        WriteLegacy("builder", "Builder", "claude-opus-5-5", "Old text.", seedHash: null);
        File.WriteAllText(Path.Combine(_seeds, "persona-seed-history.json"),
            $"{{\"builder\":[\"{HubPersonas.Hash("Builder", "claude-opus-5-5", "Old text.")}\"]}}");
        Assert.Equal("claude-sonnet-5-5", Make().Get("builder")!.Model);
    }

    [Fact]
    public void CustomPersona_WithoutASeed_IsIgnored_AndLeftOnDisk()
    {
        WriteLegacy("scout", "Scout", "claude-haiku-5-5", "Custom scout.", seedHash: null);
        var store = Make();
        Assert.Null(store.Get("scout"));
        Assert.DoesNotContain(store.List(), p => p.Id == "scout");
        Assert.True(File.Exists(FileFor("scout")));
    }

    [Fact]
    public void UnreadableOrUnknownModelOverride_IsIgnored()
    {
        Directory.CreateDirectory(_home.Paths.HubPersonasDir);
        File.WriteAllText(FileFor("builder"), "{ not json");
        File.WriteAllText(FileFor("controller"), "{\"model\":\"gpt-4\"}");
        var list = Make().List();
        Assert.All(list, p => Assert.Equal("claude-sonnet-5-5", p.Model));
    }

    [Fact]
    public void CrlfSeed_ReadsLikeLf()
    {
        File.WriteAllText(Path.Combine(_seeds, "builder", "SKILL.md"), PersonaSeedFile.Text("builder", "Builder", "claude-sonnet-5-5", false, "Build\r\nthings.", "\r\n"));
        Assert.Equal("Build\nthings.", Make().Get("builder")!.Definition);
    }

    [Fact]
    public void SharedIsNeverAPersona()
    {
        Seed("shared", "Shared", "Not a persona.");
        Assert.Null(Make().Get("shared"));
    }
}

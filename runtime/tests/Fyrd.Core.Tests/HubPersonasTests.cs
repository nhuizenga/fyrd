using Fyrd.Core.Hub;

namespace Fyrd.Core.Tests;

public sealed class HubPersonasTests : IDisposable
{
    private readonly TempHome _home = new();
    private readonly string _seeds = Path.Combine(Path.GetTempPath(), "fyrd-seeds-" + Guid.NewGuid());

    public HubPersonasTests()
    {
        Directory.CreateDirectory(_seeds);
        File.WriteAllText(Path.Combine(_seeds, "builder.md"), "---\nname: Builder\nmodel: claude-sonnet-5-5\nlocked: false\n---\nBuild things.");
        File.WriteAllText(Path.Combine(_seeds, "controller.md"), "---\nname: Controller\nmodel: claude-sonnet-5-5\nlocked: true\n---\nControl things.");
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

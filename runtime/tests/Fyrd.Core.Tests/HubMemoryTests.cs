using Fyrd.Core.Hub;

namespace Fyrd.Core.Tests;

public sealed class HubMemoryTests : IDisposable
{
    private readonly TempHome _home = new();
    public void Dispose() => _home.Dispose();
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Add_ThenList_NewestFirst()
    {
        var m = new HubMemory(_home.Paths);
        m.Add("shared", "one", "user", T0);
        m.Add("shared", "two", "user", T0.AddMinutes(1));
        Assert.Equal(["two", "one"], m.List("shared").Select(e => e.Text).ToArray());
        Assert.Equal(["two", "one"], new HubMemory(_home.Paths).All()["shared"].Select(e => e.Text).ToArray());
    }

    [Fact]
    public void Update_And_Delete_ById()
    {
        var m = new HubMemory(_home.Paths);
        var e = m.Add("builder", "one", "user", T0);
        m.Update("builder", e.Id, "uno");
        Assert.Equal("uno", m.List("builder").Single().Text);
        m.Delete("builder", e.Id);
        Assert.Empty(m.List("builder"));
        Assert.Throws<KeyNotFoundException>(() => m.Update("builder", e.Id, "x"));
        Assert.Throws<KeyNotFoundException>(() => m.Delete("builder", e.Id));
    }

    [Fact]
    public void Version_IncrementsOnWrite()
    {
        var m = new HubMemory(_home.Paths);
        var v0 = m.Version;
        var e = m.Add("shared", "a", "s", T0);
        var v1 = m.Version;
        m.Update("shared", e.Id, "b");
        var v2 = m.Version;
        m.Delete("shared", e.Id);
        Assert.True(v1 > v0 && v2 > v1 && m.Version > v2);
    }

    [Fact]
    public void Render_OldestFirstBullets()
    {
        var m = new HubMemory(_home.Paths);
        Assert.Equal("", m.Render("shared"));
        m.Add("shared", "one", "s", T0);
        m.Add("shared", "two", "s", T0.AddMinutes(1));
        Assert.Equal("- one\n- two", m.Render("shared"));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("")]
    [InlineData("A")]
    public void BadScope_Throws(string scope)
    {
        var m = new HubMemory(_home.Paths);
        Assert.Throws<ArgumentException>(() => m.Add(scope, "x", "s", T0));
        Assert.Throws<ArgumentException>(() => m.List(scope));
    }
}

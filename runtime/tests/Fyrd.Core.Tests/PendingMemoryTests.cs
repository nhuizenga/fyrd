using Fyrd.Core.Hub;

namespace Fyrd.Core.Tests;

public sealed class PendingMemoryTests : IDisposable
{
    private readonly TempHome _home = new();
    public void Dispose() => _home.Dispose();
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private (HubMemory Memory, PendingMemory Pending) Make()
    {
        var m = new HubMemory(_home.Paths);
        return (m, new PendingMemory(_home.Paths, m));
    }

    [Fact]
    public void Add_ListsNewestFirst_AndPersists()
    {
        var (_, p) = Make();
        p.Add("one", "builder-1", "s1", T0);
        p.Add("two", "builder-2", "s2", T0.AddMinutes(1));
        Assert.Equal(["two", "one"], p.List().Select(e => e.Text).ToArray());
        Assert.Equal(2, new PendingMemory(_home.Paths, new HubMemory(_home.Paths)).Count);
    }

    [Fact]
    public void Add_DedupesAgainstPendingAndShared()
    {
        var (m, p) = Make();
        m.Add(HubMemory.Shared, "known", "owner", T0);
        Assert.NotNull(p.Add("new", "b", "s", T0));
        Assert.Null(p.Add("new", "b2", "s2", T0));
        Assert.Null(p.Add("known", "b", "s", T0));
        Assert.Null(p.Add("   ", "b", "s", T0));
        Assert.Equal(1, p.Count);
    }

    [Fact]
    public void Add_ClampsTextAndCapsEntries()
    {
        var (_, p) = Make();
        Assert.Equal(PendingMemory.MaxTextLength, p.Add(new string('x', 5000), "b", "s", T0)!.Text.Length);
        for (var i = 0; i < PendingMemory.MaxEntries + 5; i++) p.Add($"entry {i}", "b", "s", T0.AddMinutes(i));
        Assert.Equal(PendingMemory.MaxEntries, p.Count);
        Assert.Equal($"entry {PendingMemory.MaxEntries + 4}", p.List()[0].Text);
        Assert.DoesNotContain(p.List(), e => e.Text == "entry 0");
    }

    [Fact]
    public void Approve_MovesToShared_AndBumpsVersion_WithOptionalEdit()
    {
        var (m, p) = Make();
        var a = p.Add("as proposed", "builder-1", "s1", T0)!;
        var b = p.Add("raw", "builder-2", "s2", T0)!;
        var v0 = m.Version;

        var added = p.Approve(a.Id, null, T0.AddHours(1));
        p.Approve(b.Id, "edited", T0.AddHours(1));

        Assert.Equal("as proposed", added.Text);
        Assert.Equal("builder-1", added.Source);
        Assert.Equal(["edited", "as proposed"], m.List(HubMemory.Shared).Select(e => e.Text).ToArray());
        Assert.True(m.Version > v0);
        Assert.Equal(0, p.Count);
    }

    [Fact]
    public void Reject_Removes_AndUnknownIdThrows()
    {
        var (m, p) = Make();
        var a = p.Add("nope", "b", "s", T0)!;
        p.Reject(a.Id);
        Assert.Equal(0, p.Count);
        Assert.Empty(m.List(HubMemory.Shared));
        Assert.Throws<KeyNotFoundException>(() => p.Reject(a.Id));
        Assert.Throws<KeyNotFoundException>(() => p.Approve(a.Id, null, T0));
    }

    [Fact]
    public void CorruptFile_ReadsAsEmpty_AndNotAMemoryScope()
    {
        var (m, p) = Make();
        File.WriteAllText(Path.Combine(_home.Paths.HubDir, "pending-memory.json"), "{ not json");
        Assert.Equal(0, p.Count);
        Assert.NotNull(p.Add("x", "b", "s", T0));
        Assert.DoesNotContain("pending-memory", m.All().Keys);
        Assert.DoesNotContain("pending", m.All().Keys);
    }
}

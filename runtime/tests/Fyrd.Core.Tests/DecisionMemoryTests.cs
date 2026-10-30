namespace Fyrd.Core.Tests;

public class DecisionMemoryTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ReadFor_ConcatenatesGlobalThenRepo()
    {
        using var home = new TempHome();
        var m = new DecisionMemory(home.Paths);
        m.Record("o/r", MemoryScope.Repo, "RepoQ", "RepoA", T, "s1");
        m.Record("o/r", MemoryScope.Global, "GlobalQ", "GlobalA", T, "s1");

        var text = m.ReadFor("o/r");

        Assert.True(text.IndexOf("GlobalQ", StringComparison.Ordinal) < text.IndexOf("RepoQ", StringComparison.Ordinal));
        Assert.Contains("  _2026-10-29 · spec s1_", text);
    }

    [Fact]
    public void ReadFor_NoFiles_ReturnsEmpty()
    {
        using var home = new TempHome();
        Assert.Equal("", new DecisionMemory(home.Paths).ReadFor("o/r"));
    }

    [Theory]
    [InlineData("../..")]
    [InlineData("./..")]
    [InlineData("nope")]
    public void Record_UnsafeRepo_Throws(string repo)
    {
        using var home = new TempHome();
        Assert.Throws<ArgumentException>(() =>
            new DecisionMemory(home.Paths).Record(repo, MemoryScope.Repo, "q", "a", T, "s1"));
    }
}

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

    [Fact]
    public void WriteFile_RoundTripsCrlfAndUnicode()
    {
        using var home = new TempHome();
        var m = new DecisionMemory(home.Paths);
        const string text = "a\r\nb \u00e9\u4e2d\U0001F600\r\n";
        m.WriteFile(MemoryScope.Global, null, text);
        m.WriteFile(MemoryScope.Repo, "o/r", text + "x");
        Assert.Equal(text, m.ReadFile(MemoryScope.Global, null));
        Assert.Equal(text + "x", m.ReadFile(MemoryScope.Repo, "o/r"));
        Assert.Throws<ArgumentException>(() => m.WriteFile(MemoryScope.Repo, null, "x"));
        Assert.Throws<ArgumentException>(() => m.WriteFile(MemoryScope.Repo, "../..", "x"));
    }

    [Fact]
    public void ListRepos_FromFiles()
    {
        using var home = new TempHome();
        var m = new DecisionMemory(home.Paths);
        m.WriteFile(MemoryScope.Global, null, "g");
        new OwnerProxy(home.Paths).Read();
        m.WriteFile(MemoryScope.Repo, "b/two", "x");
        m.WriteFile(MemoryScope.Repo, "a/one", "x");
        Assert.Equal(new[] { "a/one", "b/two" }, m.ListRepos());
    }
}

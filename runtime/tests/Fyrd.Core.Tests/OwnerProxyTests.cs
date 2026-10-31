namespace Fyrd.Core.Tests;

public class OwnerProxyTests
{
    [Fact]
    public void Read_CreatesTemplateOnce()
    {
        using var home = new TempHome();
        var p = new OwnerProxy(home.Paths);
        var first = p.Read();
        Assert.Contains("How I decide", first);
        Assert.Contains("Tests I expect", first);
        Assert.Contains("Things I never want", first);
        p.Save("custom");
        Assert.Equal("custom", new OwnerProxy(home.Paths).Read());
    }

    [Fact]
    public void Save_RoundTrips()
    {
        using var home = new TempHome();
        var p = new OwnerProxy(home.Paths);
        const string text = "a\r\nb \u00e9\u4e2d\U0001F600\r\n";
        p.Save(text);
        Assert.Equal(text, p.Read());
    }
}

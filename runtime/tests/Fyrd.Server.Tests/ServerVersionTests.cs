using System.Text.Json;

namespace Fyrd.Server.Tests;

public sealed class ServerVersionTests
{
    [Theory]
    [InlineData("0.2.57", "0.2.57")]
    [InlineData("0.2.57+3d3c42e5aac5ba805825da76410c181273ba90b1", "0.2.57")]
    public void Strip_DropsTheCommitMetadata(string informational, string expected) =>
        Assert.Equal(expected, ServerVersion.Strip(informational));

    [Fact]
    public void Current_IsThePluginVersion()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, ".claude-plugin", "plugin.json"))) dir = dir.Parent;
        Assert.NotNull(dir);
        using var plugin = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullName, ".claude-plugin", "plugin.json")));
        Assert.Equal(plugin.RootElement.GetProperty("version").GetString(), ServerVersion.Current);
        Assert.Equal($"fyrd {ServerVersion.Current}", ServerVersion.Line);
    }

    [Theory]
    [InlineData(new[] { "--version" }, true)]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "--urls", "http://127.0.0.1:4870" }, false)]
    [InlineData(new[] { "--version", "--urls", "http://127.0.0.1:4870" }, false)]
    public void IsVersionRequest_OnlyForABareVersionFlag(string[] args, bool expected) =>
        Assert.Equal(expected, ServerVersion.IsVersionRequest(args));
}

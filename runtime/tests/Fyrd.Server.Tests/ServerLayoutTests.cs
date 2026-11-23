namespace Fyrd.Server.Tests;

public sealed class ServerLayoutTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fyrd-layout-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string Dir(params string[] parts)
    {
        var dir = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string Packaged()
    {
        var dir = Dir("tool", "tools", "net10.0", "any");
        File.WriteAllText(Path.Combine(Dir("tool", "tools", "net10.0", "any", "wwwroot"), "index.html"), "<html></html>");
        return dir;
    }

    [Fact]
    public void BuildOutput_KeepsTheDefaultContentRoot_AndFindsSkillsAtTheRepoRoot()
    {
        var project = Dir("repo", "runtime", "src", "Fyrd.Server");
        var bin = Dir("repo", "runtime", "src", "Fyrd.Server", "bin", "Release", "net10.0");

        Assert.False(ServerLayout.IsPackaged(bin));
        Assert.Null(ServerLayout.ContentRoot(bin));
        Assert.Equal(Path.Combine(_root, "repo", "skills"), ServerLayout.DefaultDir(project, bin, "skills"));
        Assert.Equal(Path.Combine(_root, "repo", "docs"), ServerLayout.DefaultDir(project, bin, "docs"));
    }

    [Fact]
    public void Package_UsesTheAssemblyFolder_ForContentRootSkillsAndDocs()
    {
        var baseDir = Packaged();

        Assert.True(ServerLayout.IsPackaged(baseDir));
        Assert.Equal(baseDir, ServerLayout.ContentRoot(baseDir));
        Assert.Equal(Path.Combine(baseDir, "skills"), ServerLayout.DefaultDir(baseDir, baseDir, "skills"));
        Assert.Equal(Path.Combine(baseDir, "docs"), ServerLayout.DefaultDir(baseDir + Path.DirectorySeparatorChar, baseDir, "docs"));
    }

    [Fact]
    public void Package_WithAContentRootGivenOnTheCommandLine_KeepsTheRepoLayoutForThatRoot()
    {
        var baseDir = Packaged();
        var project = Dir("repo", "runtime", "src", "Fyrd.Server");

        Assert.Equal(Path.Combine(_root, "repo", "skills"), ServerLayout.DefaultDir(project, baseDir, "skills"));
    }
}

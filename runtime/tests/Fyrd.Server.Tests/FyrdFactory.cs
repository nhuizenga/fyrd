using Fyrd.Core.CloudSessions;
using Fyrd.Core.Config;
using Fyrd.Core.Hub;
using Fyrd.Core.Tests;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Fyrd.Server.Tests;

public sealed class FyrdFactory : WebApplicationFactory<Program>
{
    public FyrdFactory() => Home = new TempHome();

    public TempHome Home { get; }

    /// <summary>Set before the first request to point the server at another docs folder.</summary>
    public string? DocsDir { get; init; }

    public FakeCloudSessions Cloud { get; } = new();

    public FakeGitHub GitHub { get; } = new();

    public FakeOwnerMessages Owner { get; } = new();

    public FakeFiles Files { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Fyrd:Home", Home.Paths.Root);
        builder.UseSetting("Fyrd:DisableRunner", "true");
        builder.UseSetting("Fyrd:HubPersonasSeedDir", Path.Combine(RepoRoot(), "personas", "hub"));
        builder.UseSetting("Fyrd:SessionSkillsSeedDir", Path.Combine(RepoRoot(), "skills"));
        builder.UseSetting("Fyrd:DocsDir", DocsDir ?? Path.Combine(RepoRoot(), "docs"));
        builder.ConfigureTestServices(s =>
        {
            s.RemoveAll<ICloudSessions>();
            s.AddSingleton<ICloudSessions>(Cloud);
            s.RemoveAll<ISecretProtector>();
            s.AddSingleton<ISecretProtector>(new FakeSecretProtector());
            s.RemoveAll<IGitHub>();
            s.AddSingleton<IGitHub>(GitHub);
            s.RemoveAll<IOwnerMessages>();
            s.AddSingleton<IOwnerMessages>(Owner);
            s.RemoveAll<IFiles>();
            s.AddSingleton<IFiles>(Files);
        });
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "personas", "hub"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("personas/hub not found above test output");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) Home.Dispose();
    }
}

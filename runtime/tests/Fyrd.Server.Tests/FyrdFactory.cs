using Fyrd.Core.CloudSessions;
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

    public FakeCloudSessions Cloud { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Fyrd:Home", Home.Paths.Root);
        builder.UseSetting("Fyrd:DisableRunner", "true");
        builder.UseSetting("Fyrd:PersonasDir", Path.Combine(RepoRoot(), "personas"));
        builder.ConfigureTestServices(s =>
        {
            s.RemoveAll<ICloudSessions>();
            s.AddSingleton<ICloudSessions>(Cloud);
        });
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "personas", "lead.md"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("personas/lead.md not found above test output");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) Home.Dispose();
    }
}

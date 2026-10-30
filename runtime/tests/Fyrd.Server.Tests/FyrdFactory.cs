using Fyrd.Core.Tests;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Fyrd.Server.Tests;

public sealed class FyrdFactory : WebApplicationFactory<Program>
{
    public FyrdFactory() => Home = new TempHome();

    public TempHome Home { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseSetting("Fyrd:Home", Home.Paths.Root);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) Home.Dispose();
    }
}

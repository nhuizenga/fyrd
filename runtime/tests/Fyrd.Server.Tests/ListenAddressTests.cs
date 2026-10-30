using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Fyrd.Server.Tests;

public class ListenAddressTests
{
    [Fact]
    public void Appsettings_DoesNotDefineKestrelEndpoints_SoUrlsFlagWins()
    {
        using var factory = new FyrdFactory();
        var config = factory.Services.GetRequiredService<IConfiguration>();

        // Kestrel endpoints from configuration override --urls; they must not be configured.
        Assert.False(config.GetSection("Kestrel:Endpoints").Exists());
    }

    [Fact]
    public void Appsettings_DefaultsToLoopback4870()
    {
        using var factory = new FyrdFactory();
        var config = factory.Services.GetRequiredService<IConfiguration>();

        Assert.Equal("http://127.0.0.1:4870", config["Urls"]);
    }
}

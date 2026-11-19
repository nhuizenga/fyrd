using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Fyrd.Server.Tests;

public class ApiTests
{
    private static async Task<JsonElement> ReadJson(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private static HttpRequestMessage PutConfig(object body) =>
        new(HttpMethod.Put, "/api/config") { Content = JsonContent.Create(body) };

    private static async Task<int> SessionCap(HttpClient c) =>
        (await ReadJson(await c.GetAsync("/api/config"))).GetProperty("sessionCap").GetInt32();

    [Fact]
    public async Task Health_Ok()
    {
        using var f = new FyrdFactory();
        var r = await f.CreateClient().GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("ok", (await ReadJson(r)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Write_WithForeignOrigin_Returns403()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        var req = PutConfig(new { sessionCap = 7 });
        req.Headers.Add("Origin", "http://evil.example");
        var r = await c.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal(20, await SessionCap(c));
    }

    [Fact]
    public async Task Write_WithForeignReferer_Returns403()
    {
        using var f = new FyrdFactory();
        var req = PutConfig(new { sessionCap = 7 });
        req.Headers.Add("Referer", "http://evil.example/page");
        Assert.Equal(HttpStatusCode.Forbidden, (await f.CreateClient().SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task Write_WithMatchingOrigin_IsAllowed()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        var req = PutConfig(new { sessionCap = 7 });
        req.Headers.Add("Origin", "http://localhost");
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(req)).StatusCode);
        Assert.Equal(7, await SessionCap(c));
    }

    [Fact]
    public async Task Request_WithForeignHost_Returns400()
    {
        using var f = new FyrdFactory();
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/config");
        req.Headers.Host = "evil.example";
        Assert.Equal(HttpStatusCode.BadRequest, (await f.CreateClient().SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task PutConfig_OutOfRange_Returns400_AndKeepsOld()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        var bad = await c.PutAsJsonAsync("/api/config", new { sessionCap = 101 });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(20, await SessionCap(c));

        var ok = await c.PutAsJsonAsync("/api/config", new { sessionCap = 6 });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(6, await SessionCap(c));
    }

    [Fact]
    public async Task UnknownApiPath_Returns404_NotIndexHtml()
    {
        using var f = new FyrdFactory();
        var r = await f.CreateClient().GetAsync("/api/nope");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.DoesNotContain("<html", await r.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }
}

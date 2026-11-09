using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Fyrd.Server.Tests;

public class SessionOrderApiTests
{
    [Fact]
    public async Task Get_IsEmptyByDefault()
    {
        using var f = new FyrdFactory();
        var doc = JsonDocument.Parse(await f.CreateClient().GetStringAsync("/api/session-order")).RootElement;
        Assert.Equal(0, doc.GetProperty("sessions").GetArrayLength());
        Assert.Equal(0, doc.GetProperty("groups").GetArrayLength());
    }

    [Fact]
    public async Task Put_SavesCleanedOrder_AndGetReturnsIt()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        var put = await c.PutAsJsonAsync("/api/session-order", new { sessions = new[] { "b", "a", "b", " " }, groups = new[] { "builder", "validator" } });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var doc = JsonDocument.Parse(await c.GetStringAsync("/api/session-order")).RootElement;
        Assert.Equal(new[] { "b", "a" }, doc.GetProperty("sessions").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(new[] { "builder", "validator" }, doc.GetProperty("groups").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public async Task Put_RejectsOversizedLists()
    {
        using var f = new FyrdFactory();
        var many = Enumerable.Range(0, 2001).Select(i => "s" + i).ToArray();
        var r = await f.CreateClient().PutAsJsonAsync("/api/session-order", new { sessions = many, groups = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }
}

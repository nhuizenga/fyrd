using System.Net;
using System.Text.Json;
using Fyrd.Core.CloudSessions;
using Fyrd.Core.Hub;
using Fyrd.Core.Tests;
using Microsoft.Extensions.DependencyInjection;

namespace Fyrd.Server.Tests;

public class HubExportTests
{
    private const string Id = "session_hub1";

    private static void Seed(FyrdFactory f, int events, string title = "builder-abc", int pageSize = 3, HubCostView? cost = null)
    {
        f.Cloud.PageSize = pageSize;
        f.Cloud.SetSession(FakeCloudSessions.Session(Id));
        for (var i = 1; i <= events; i++)
            f.Cloud.AddEvent(Id, new SessionEvent($"e{i}", "assistant", DateTimeOffset.UnixEpoch, null,
                JsonDocument.Parse($"{{\"uuid\":\"e{i}\",\"type\":\"assistant\",\"n\":{i}}}").RootElement.Clone()));
        f.Services.GetRequiredService<HubState>().Publish(
            [new HubSessionView(Id, title, "builder", "running", null, null, null, null, ["fyrd-hub"], cost)], []);
    }

    private static string[] Lines(string body) => body.TrimEnd('\n').Split('\n');

    [Fact]
    public async Task Export_WritesHeaderEventsAndTrailer()
    {
        using var f = new FyrdFactory();
        Seed(f, 7);
        var r = await f.CreateClient().GetAsync($"/api/hub/sessions/{Id}/export");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.StartsWith("application/x-ndjson", r.Content.Headers.ContentType!.ToString());
        Assert.EndsWith(".jsonl", r.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        Assert.Equal("attachment", r.Content.Headers.ContentDisposition.DispositionType);
        var lines = Lines(await r.Content.ReadAsStringAsync());
        Assert.Equal(9, lines.Length);
        var head = JsonDocument.Parse(lines[0]).RootElement;
        Assert.Equal("fyrd_export", head.GetProperty("type").GetString());
        Assert.Equal(Id, head.GetProperty("sessionId").GetString());
        Assert.Equal("builder", head.GetProperty("role").GetString());
        Assert.Equal(1, head.GetProperty("version").GetInt32());
        for (var i = 1; i <= 7; i++)
            Assert.Equal($"{{\"uuid\":\"e{i}\",\"type\":\"assistant\",\"n\":{i}}}", lines[i]);
        var end = JsonDocument.Parse(lines[8]).RootElement;
        Assert.Equal("fyrd_export_end", end.GetProperty("type").GetString());
        Assert.Equal(7, end.GetProperty("eventCount").GetInt32());
    }

    [Fact]
    public async Task Export_NoEvents_HeaderAndTrailerOnly()
    {
        using var f = new FyrdFactory();
        Seed(f, 0);
        var lines = Lines(await f.CreateClient().GetStringAsync($"/api/hub/sessions/{Id}/export"));
        Assert.Equal(2, lines.Length);
        Assert.Equal(0, JsonDocument.Parse(lines[1]).RootElement.GetProperty("eventCount").GetInt32());
    }

    [Fact]
    public async Task Export_NotHubSession_404()
    {
        using var f = new FyrdFactory();
        var r = await f.CreateClient().GetAsync("/api/hub/sessions/nope/export");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Export_AuthFailureFirst_502SignedOut()
    {
        using var f = new FyrdFactory();
        Seed(f, 2);
        f.Cloud.ThrowOnNextCall(new CloudSessionAuthException(401));
        var r = await f.CreateClient().GetAsync($"/api/hub/sessions/{Id}/export");
        Assert.Equal(HttpStatusCode.BadGateway, r.StatusCode);
        Assert.Contains("claude auth login", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Export_ApiFailureFirst_502()
    {
        using var f = new FyrdFactory();
        Seed(f, 2);
        f.Cloud.ThrowOnNextCall(new CloudSessionApiException(500, "boom"));
        var r = await f.CreateClient().GetAsync($"/api/hub/sessions/{Id}/export");
        Assert.Equal(HttpStatusCode.BadGateway, r.StatusCode);
    }

    [Fact]
    public async Task Export_LaterPageFails_NoTrailer()
    {
        using var f = new FyrdFactory();
        Seed(f, 7);
        // GetAsync + first page succeed; the second page throws.
        f.Cloud.SkipCalls(2);
        f.Cloud.ThrowOnNextCall(new CloudSessionApiException(500, "boom"));
        string? body = null;
        try { body = await f.CreateClient().GetStringAsync($"/api/hub/sessions/{Id}/export"); }
        catch (Exception) { /* aborted response */ }
        Assert.DoesNotContain("fyrd_export_end", body ?? "");
    }

    [Fact]
    public async Task Export_BlockedSession_StillOk()
    {
        using var f = new FyrdFactory();
        Seed(f, 1, cost: new HubCostView(10, 5, 2, false, "session", false, false));
        var r = await f.CreateClient().GetAsync($"/api/hub/sessions/{Id}/export");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Theory]
    [InlineData("Build the Thing!  ", "build-the-thing")]
    [InlineData("日本語 タイトル", "session")]
    [InlineData("", "session")]
    public async Task FileName_IsSafe(string title, string slug)
    {
        using var f = new FyrdFactory();
        Seed(f, 0, title);
        var r = await f.CreateClient().GetAsync($"/api/hub/sessions/{Id}/export");
        var cd = string.Join(" ", r.Content.Headers.GetValues("Content-Disposition"));
        Assert.Matches($"filename=\"?{slug}-{Id}-\\d{{8}}-\\d{{6}}\\.jsonl", cd);
        Assert.Matches("filename\\*=UTF-8''" + slug + "-", cd);
    }
}

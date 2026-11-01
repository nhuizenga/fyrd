using System.Net;
using System.Text.Json;
using Fyrd.Core.CloudSessions;
using Fyrd.Core.Hub;
using Microsoft.Extensions.DependencyInjection;

namespace Fyrd.Server.Tests;

public class HubStreamTests
{
    private const string SessionId = "session_hub1";

    private static StreamedEvent Event(long seq, string payload)
    {
        var raw = JsonDocument.Parse(payload).RootElement.Clone();
        return new StreamedEvent(seq, new SessionEvent("u" + seq, raw.GetProperty("type").GetString()!,
            DateTimeOffset.UnixEpoch.AddSeconds(seq), null, raw));
    }

    private static FyrdFactory Hub()
    {
        var f = new FyrdFactory();
        f.Services.GetRequiredService<HubState>().Publish(
            [new HubSessionView(SessionId, "builder-abc", "builder", "running", null, null, null, null, ["fyrd-hub"])], []);
        f.Cloud.Streams[SessionId] =
        [
            Event(1, """{"type":"user","message":{"content":"Build it"}}"""),
            Event(2, """{"type":"system","subtype":"init"}"""),
            Event(3, """
                {"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_1","name":"AskUserQuestion",
                 "input":{"questions":[{"question":"Pick a fruit","header":"Fruit","multiSelect":false,"options":[{"label":"Apple"}]}]}}]}}
                """),
        ];
        return f;
    }

    private sealed record Frame(string? Id, string? Event, string? Data);

    /// <summary>Reads SSE frames until <paramref name="count"/> named frames arrive; the timeout keeps a bad server from hanging the test.</summary>
    private static async Task<(List<Frame> Frames, string Raw, HttpResponseMessage Response)> ReadFrames(
        HttpClient c, int count, string? lastEventId = null, Action<int>? onFrame = null)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/hub/sessions/{SessionId}/stream");
        if (lastEventId is not null) req.Headers.Add("Last-Event-ID", lastEventId);
        var resp = await c.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        var frames = new List<Frame>();
        var raw = new System.Text.StringBuilder();
        await using var body = await resp.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(body);
        string? id = null, ev = null, data = null;
        while (frames.Count < count)
        {
            var line = await reader.ReadLineAsync(cts.Token) ?? throw new EndOfStreamException();
            raw.Append(line).Append('\n');
            if (line.Length == 0)
            {
                if (ev is not null)
                {
                    frames.Add(new Frame(id, ev, data));
                    onFrame?.Invoke(frames.Count);
                }
                id = ev = data = null;
            }
            else if (line.StartsWith("id: ")) id = line[4..];
            else if (line.StartsWith("event: ")) ev = line[7..];
            else if (line.StartsWith("data: ")) data = line[6..];
        }
        cts.Cancel();
        return (frames, raw.ToString(), resp);
    }

    [Fact]
    public async Task Stream_NotHubSession_404()
    {
        using var f = new FyrdFactory();
        var r = await f.CreateClient().GetAsync("/api/hub/sessions/session_other/stream");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Stream_RelaysTranscriptItems()
    {
        using var f = Hub();
        var (frames, raw, resp) = await ReadFrames(f.CreateClient(), 2);

        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-cache", resp.Headers.CacheControl!.ToString());
        Assert.StartsWith(": connected\n\n", raw);
        Assert.All(frames, fr => Assert.Equal("item", fr.Event));
        Assert.Equal(["1", "3"], frames.Select(fr => fr.Id));

        var user = JsonDocument.Parse(frames[0].Data!).RootElement;
        Assert.Equal("1:0", user.GetProperty("key").GetString());
        Assert.Equal(1, user.GetProperty("seq").GetInt64());
        Assert.Equal("user", user.GetProperty("role").GetString());
        Assert.Equal("text", user.GetProperty("kind").GetString());
        Assert.Equal("Build it", user.GetProperty("text").GetString());

        var q = JsonDocument.Parse(frames[1].Data!).RootElement;
        Assert.Equal("agent", q.GetProperty("role").GetString());
        Assert.Equal("question", q.GetProperty("kind").GetString());
        Assert.Equal("toolu_1", q.GetProperty("toolUseId").GetString());
        var question = q.GetProperty("questions")[0];
        Assert.Equal("Pick a fruit", question.GetProperty("question").GetString());
        Assert.False(question.GetProperty("multiSelect").GetBoolean());
        Assert.Equal("Apple", question.GetProperty("options")[0].GetProperty("label").GetString());
    }

    [Fact]
    public async Task Stream_LastEventId_ResumesAfterIt()
    {
        using var f = Hub();
        var (frames, _, _) = await ReadFrames(f.CreateClient(), 1, lastEventId: "1");
        Assert.Equal("3", Assert.Single(frames).Id);
        Assert.Equal(1L, f.Cloud.StreamAfter[0]);
    }

    [Fact]
    public async Task Stream_DuplicateSequence_Dropped()
    {
        using var f = Hub();
        f.Cloud.Streams[SessionId].Add(Event(3, """{"type":"user","message":{"content":"Dup"}}"""));
        f.Cloud.Streams[SessionId].Add(Event(4, """{"type":"user","message":{"content":"More"}}"""));
        var (frames, _, _) = await ReadFrames(f.CreateClient(), 3);
        Assert.Equal(["1", "3", "4"], frames.Select(fr => fr.Id));
        Assert.DoesNotContain(frames, fr => fr.Data!.Contains("Dup"));
    }

    [Fact]
    public async Task Stream_UpstreamEnds_ReopensAfterLastSequence()
    {
        using var f = Hub();
        // The fake stream ends after seq 3; the next event appears only on a later connection.
        var (frames, _, _) = await ReadFrames(f.CreateClient(), 3, onFrame: n =>
        {
            if (n == 2)
                lock (f.Cloud.Streams) f.Cloud.Streams[SessionId].Add(Event(4, """{"type":"user","message":{"content":"More"}}"""));
        });
        Assert.Equal(["1", "3", "4"], frames.Select(fr => fr.Id));
        Assert.Null(f.Cloud.StreamAfter[0]);
        Assert.Contains(3L, f.Cloud.StreamAfter);
    }

    [Fact]
    public async Task Stream_SignedOut_FaultAndEnds()
    {
        using var f = Hub();
        f.Cloud.ThrowOnNextCall(new CloudSessionAuthException(401));
        var (frames, _, _) = await ReadFrames(f.CreateClient(), 1);
        var fault = Assert.Single(frames);
        Assert.Equal("fault", fault.Event);
        Assert.Contains("claude auth login", JsonDocument.Parse(fault.Data!).RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Stream_ApiError_FaultThenRetries()
    {
        using var f = Hub();
        f.Cloud.ThrowOnNextCall(new CloudSessionApiException(500, "boom"));
        var (frames, _, _) = await ReadFrames(f.CreateClient(), 3);
        Assert.Equal(["fault", "item", "item"], frames.Select(fr => fr.Event));
    }
}

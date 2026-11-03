using System.Text.Json;
using Fyrd.Core.CloudSessions;

namespace Fyrd.Core.Tests;

public class CloudSessionPagingTests
{
    private static FakeCloudSessions Fake(int n, int pageSize)
    {
        var f = new FakeCloudSessions { PageSize = pageSize };
        for (var i = 1; i <= n; i++)
            f.AddEvent("s", new SessionEvent($"e{i}", "assistant", DateTimeOffset.UnixEpoch, null, FakeCloudSessions.Empty));
        return f;
    }

    private static async Task<List<string>> Ids(IAsyncEnumerable<SessionEvent> events, CancellationToken ct = default)
    {
        var ids = new List<string>();
        await foreach (var e in events.WithCancellation(ct)) ids.Add(e.Uuid);
        return ids;
    }

    [Fact]
    public async Task YieldsEveryEventInOrderAcrossPages()
    {
        var f = Fake(7, 3);
        Assert.Equal(["e1", "e2", "e3", "e4", "e5", "e6", "e7"], await Ids(f.AllEventsAsync("s")));
        Assert.Equal(3, f.EventAfterIds.Count);
    }

    [Fact]
    public async Task StopsWhenNoMore_AndEmptyYieldsNothing()
    {
        var f = Fake(3, 3);
        await Ids(f.AllEventsAsync("s"));
        Assert.Single(f.EventAfterIds);
        Assert.Empty(await Ids(Fake(0, 3).AllEventsAsync("s")));
    }

    [Fact]
    public async Task StartsAfterGivenId()
    {
        Assert.Equal(["e3", "e4"], await Ids(Fake(4, 10).AllEventsAsync("s", "e2")));
    }

    [Fact]
    public async Task StopsWhenLastIdDoesNotAdvance()
    {
        var f = new FakeStuck();
        Assert.Equal(["x"], await Ids(f.AllEventsAsync("s", "x")));
        Assert.Equal(1, f.Calls);
    }

    [Fact]
    public async Task StopsOnCancellation()
    {
        var f = Fake(10, 2);
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var e in f.AllEventsAsync("s", ct: cts.Token))
                if (e.Uuid == "e2") cts.Cancel();
        });
        Assert.Equal(1, f.EventAfterIds.Count);
    }

    /// <summary>Claims more events but always reports the same last id.</summary>
    private sealed class FakeStuck : ICloudSessions
    {
        public int Calls;
        public Task<EventPage> ListEventsAsync(string sessionId, int limit = 100, string? afterId = null, string? beforeId = null, CancellationToken ct = default)
        {
            Calls++;
            var e = new SessionEvent("x", "assistant", DateTimeOffset.UnixEpoch, null, FakeCloudSessions.Empty);
            return Task.FromResult(new EventPage([e], true, "x", "x"));
        }
        public Task<CloudSession> CreateAsync(CreateSessionRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<CloudSession> GetAsync(string sessionId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<CloudSession>> ListByTagAsync(string tag, CancellationToken ct = default) => throw new NotImplementedException();
        public Task InterruptAsync(string sessionId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ArchiveAsync(string sessionId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RenameAsync(string sessionId, string title, CancellationToken ct = default) => throw new NotImplementedException();
        public Task AnswerQuestionAsync(string sessionId, Fyrd.Core.Hub.HubQuestion question, IReadOnlyDictionary<string, string> answers, CancellationToken ct = default) => throw new NotImplementedException();
        public IAsyncEnumerable<StreamedEvent> StreamEventsAsync(string sessionId, long? afterSequence, CancellationToken ct = default) => throw new NotImplementedException();
    }
}

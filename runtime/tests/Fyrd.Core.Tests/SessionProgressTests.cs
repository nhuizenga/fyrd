using System.Text.Json;
using Fyrd.Core.CloudSessions;
using Fyrd.Core.Hub;

namespace Fyrd.Core.Tests;

public class SessionProgressTests
{
    private static HubSessionView View(string id, string status = "running") =>
        new(id, id, "builder", status, null, null, null, null, ["fyrd-hub"]);

    private static StreamedEvent Ephemeral(string payload)
    {
        var raw = JsonDocument.Parse(payload).RootElement.Clone();
        return new StreamedEvent(0, new SessionEvent("", raw.GetProperty("type").GetString()!, DateTimeOffset.UnixEpoch, null, raw), Ephemeral: true);
    }

    private static StreamedEvent Saved(long seq)
    {
        var raw = JsonDocument.Parse("""{"type":"user","message":{"content":"hi"}}""").RootElement.Clone();
        return new StreamedEvent(seq, new SessionEvent("u" + seq, "user", DateTimeOffset.UnixEpoch.AddSeconds(seq), null, raw));
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Condition was not met.");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Watcher_KeepsProgressForSessionsNobodyHasOpen()
    {
        var cloud = new FakeCloudSessions();
        cloud.Streams["session_a"] = [Ephemeral("""{"type":"system","subtype":"task_summary","detail":"Running the tests"}""")];
        cloud.Streams["session_b"] = [Ephemeral("""{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"Hello"}}}""")];
        var store = new SessionProgress();
        using var watcher = new SessionProgressWatcher(cloud, store);

        watcher.Sync([View("session_a"), View("session_b")]);

        await Eventually(() => store.Get("session_a") is not null && store.Get("session_b") is not null);
        Assert.Equal("Running the tests", store.Get("session_a")!.Status);
        Assert.Equal("Hello", store.Get("session_b")!.Text);
        Assert.Equal(["session_a", "session_b"], watcher.Watching.Order());
        Assert.Equal(["session_a", "session_b"], store.Snapshot().Keys.Order());
    }

    [Fact]
    public async Task Watcher_LogsLivePostTurnSummaries_Only()
    {
        using var h = new TempHome();
        var log = new SummaryLog(h.Paths);
        var cloud = new FakeCloudSessions();
        cloud.Streams["session_a"] =
        [
            Ephemeral("""{"type":"system","subtype":"task_summary","detail":"Working"}"""),
            Ephemeral("""{"type":"system","subtype":"post_turn_summary","status_detail":"done","is_noteworthy":true}"""),
        ];
        using var watcher = new SessionProgressWatcher(cloud, new SessionProgress(), log);

        watcher.Sync([View("session_a")]);

        await Eventually(() => File.Exists(log.FilePath));
        var line = JsonDocument.Parse(Assert.Single(File.ReadAllLines(log.FilePath))).RootElement;
        Assert.Equal("stream", line.GetProperty("source").GetString());
        Assert.Equal("session_a", line.GetProperty("sessionId").GetString());
        Assert.True(line.GetProperty("summary").GetProperty("is_noteworthy").GetBoolean());
    }

    [Fact]
    public async Task Watcher_SavedEventClearsTheLine()
    {
        var cloud = new FakeCloudSessions();
        cloud.Streams["session_a"] =
        [
            Ephemeral("""{"type":"system","subtype":"task_summary","detail":"Working"}"""),
            Saved(1),
        ];
        var store = new SessionProgress();
        using var watcher = new SessionProgressWatcher(cloud, store);

        watcher.Sync([View("session_a")]);

        // The stream ends after the replay and is reopened after the saved event's sequence.
        await Eventually(() => cloud.StreamAfter.Contains(1));
        Assert.Null(store.Get("session_a"));
    }

    [Fact]
    public async Task Watcher_StopsWatchingArchivedAndRemovedSessions_AndDropsTheirProgress()
    {
        var cloud = new FakeCloudSessions();
        cloud.Streams["session_a"] = [Ephemeral("""{"type":"system","subtype":"task_summary","detail":"A"}""")];
        cloud.Streams["session_b"] = [Ephemeral("""{"type":"system","subtype":"task_summary","detail":"B"}""")];
        var store = new SessionProgress();
        using var watcher = new SessionProgressWatcher(cloud, store);
        watcher.Sync([View("session_a"), View("session_b")]);
        await Eventually(() => store.Snapshot().Count == 2);

        watcher.Sync([View("session_a", "archived")]);

        Assert.Empty(watcher.Watching);
        Assert.Empty(store.Snapshot());
    }

    [Fact]
    public async Task Watcher_OpensOneStreamPerSession_NotOnePerSync()
    {
        var cloud = new FakeCloudSessions();
        cloud.Streams["session_a"] = [];
        var store = new SessionProgress();
        using var watcher = new SessionProgressWatcher(cloud, store);

        for (var i = 0; i < 5; i++) watcher.Sync([View("session_a")]);

        await Eventually(() => cloud.StreamAfter.Count >= 1);
        Assert.Single(watcher.Watching);
        // Reopens are paced by the backoff delay, so five syncs cannot have produced five opens.
        Assert.True(cloud.StreamAfter.Count < 3);
    }

    [Fact]
    public void Store_EmptyItemRemovesTheEntry()
    {
        var store = new SessionProgress();
        var live = new TranscriptItem("ephemeral", 0, "agent", "progress", "", DateTimeOffset.UnixEpoch, Status: "Thinking…");
        store.Set("s", live);
        Assert.Equal(live, store.Get("s"));
        store.Set("s", live with { Status = null });
        Assert.Null(store.Get("s"));
    }
}

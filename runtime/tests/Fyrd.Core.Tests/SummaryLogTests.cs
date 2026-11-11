using System.Text.Json;
using Fyrd.Core.Hub;

namespace Fyrd.Core.Tests;

public sealed class SummaryLogTests
{
    private static JsonElement Session(bool noteworthy, string title = "Decision needed") =>
        JsonDocument.Parse(
            """{"id":"s1","session_status":"idle","post_turn_summary":{"is_noteworthy":"""
            + (noteworthy ? "true" : "false")
            + ""","title":""" + JsonSerializer.Serialize(title) + ""","description":"d","needs_action":""}}""")
            .RootElement.Clone();

    private static List<JsonElement> Lines(SummaryLog log) =>
        File.Exists(log.FilePath)
            ? File.ReadAllLines(log.FilePath).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList()
            : [];

    [Fact]
    public void RecordPolled_LogsTopLevelSummaryWithStatusAndTitle()
    {
        using var h = new TempHome();
        var log = new SummaryLog(h.Paths);

        log.RecordPolled("s1", "builder-x", Session(noteworthy: true));

        var line = Assert.Single(Lines(log));
        Assert.Equal("poll", line.GetProperty("source").GetString());
        Assert.Equal("s1", line.GetProperty("sessionId").GetString());
        Assert.Equal("builder-x", line.GetProperty("title").GetString());
        Assert.Equal("idle", line.GetProperty("sessionStatus").GetString());
        Assert.True(line.GetProperty("summary").GetProperty("is_noteworthy").GetBoolean());
    }

    [Fact]
    public void RecordPolled_SameSummaryTwice_LogsOnce_ChangeLogsAgain()
    {
        using var h = new TempHome();
        var log = new SummaryLog(h.Paths);

        log.RecordPolled("s1", null, Session(noteworthy: true));
        log.RecordPolled("s1", null, Session(noteworthy: true));
        log.RecordPolled("s1", null, Session(noteworthy: false));

        Assert.Equal(2, Lines(log).Count);
    }

    [Fact]
    public void RecordPolled_NoSummary_LogsNothing()
    {
        using var h = new TempHome();
        var log = new SummaryLog(h.Paths);

        log.RecordPolled("s1", null, JsonDocument.Parse("""{"id":"s1"}""").RootElement.Clone());

        Assert.Empty(Lines(log));
    }

    [Fact]
    public void RecordStreamed_LogsRawEventEveryTime()
    {
        using var h = new TempHome();
        var log = new SummaryLog(h.Paths);
        var raw = JsonDocument.Parse("""{"type":"system","subtype":"post_turn_summary","status_detail":"done"}""").RootElement.Clone();
        var at = DateTimeOffset.Parse("2026-11-11T00:59:36Z");

        log.RecordStreamed("s1", at, raw);
        log.RecordStreamed("s1", at, raw);

        var lines = Lines(log);
        Assert.Equal(2, lines.Count);
        Assert.Equal("stream", lines[0].GetProperty("source").GetString());
        Assert.Equal("done", lines[0].GetProperty("summary").GetProperty("status_detail").GetString());
        Assert.StartsWith("2026-11-11T00:59:36", lines[0].GetProperty("eventAt").GetString());
    }
}

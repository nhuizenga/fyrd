using System.Text.Json;
using Fyrd.Core.CloudSessions;
using Fyrd.Core.Hub;

namespace Fyrd.Core.Tests;

public class ProgressTrackerTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static StreamedEvent Eph(string payload)
    {
        var raw = JsonDocument.Parse(payload).RootElement.Clone();
        return new StreamedEvent(0, new SessionEvent("", raw.GetProperty("type").GetString()!, At, null, raw), Ephemeral: true);
    }

    private static string Delta(string text) =>
        """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":""" + JsonSerializer.Serialize(text) + "}}}";

    private const string MessageStart = """{"type":"stream_event","event":{"type":"message_start","message":{"content":[]}}}""";
    private const string ToolStart = """{"type":"stream_event","event":{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","name":"Read"}}}""";
    private const string ThinkingStart = """{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"thinking"}}}""";
    private const string TextStart = """{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"text"}}}""";

    [Fact]
    public void TaskSummary_SetsStatus_AsAProgressItem()
    {
        var item = new ProgressTracker().Apply(Eph("""{"type":"system","subtype":"task_summary","detail":"Running grep -rn foo…"}"""))!;
        Assert.Equal(new TranscriptItem("ephemeral", 0, "agent", "progress", "", At, Status: "Running grep -rn foo…"), item);
    }

    [Fact]
    public void StreamedText_Accumulates_AndShowsAlongsideTheToolStatus()
    {
        var t = new ProgressTracker();
        t.Apply(Eph(MessageStart));
        t.Apply(Eph(TextStart));
        t.Apply(Eph(Delta("Tests first: ")));
        var item = t.Apply(Eph(Delta("the `gh` parsing.")))!;
        Assert.Equal("Tests first: the `gh` parsing.", item.Text);

        item = t.Apply(Eph(ToolStart))!;
        Assert.Equal("Using Read", item.Status);
        Assert.Equal("Tests first: the `gh` parsing.", item.Text);
    }

    [Fact]
    public void Thinking_ShowsAThinkingStatus()
    {
        Assert.Equal("Thinking…", new ProgressTracker().Apply(Eph(ThinkingStart))!.Status);
    }

    [Fact]
    public void TaskSummary_WinsOverBlockStatus_AndNullDetailClearsIt()
    {
        var t = new ProgressTracker();
        t.Apply(Eph(ToolStart));
        Assert.Equal("Running grep", t.Apply(Eph("""{"type":"system","subtype":"task_summary","detail":"Running grep"}"""))!.Status);
        Assert.Equal("Using Read", t.Apply(Eph("""{"type":"system","subtype":"task_summary","detail":null}"""))!.Status);
    }

    [Fact]
    public void MessageStart_ResetsTheStreamedText()
    {
        var t = new ProgressTracker();
        t.Apply(Eph(Delta("first")));
        Assert.Equal("", t.Apply(Eph(MessageStart))!.Text);
    }

    [Fact]
    public void SavedEvent_ClearsActiveProgress_OnceThenNothing()
    {
        var t = new ProgressTracker();
        t.Apply(Eph(Delta("hello")));
        var cleared = t.Reset(At)!;
        Assert.Equal(("progress", "", ""), (cleared.Kind, cleared.Text, cleared.Status ?? ""));
        Assert.Null(t.Reset(At));
    }

    [Fact]
    public void PostTurnSummary_ClearsProgress()
    {
        var t = new ProgressTracker();
        t.Apply(Eph(Delta("hello")));
        var cleared = t.Apply(Eph("""{"type":"system","subtype":"post_turn_summary","status_detail":"done"}"""))!;
        Assert.Equal(("", ""), (cleared.Text, cleared.Status ?? ""));
        Assert.Null(t.Reset(At));
    }

    [Fact]
    public void IrrelevantOrIdleEvents_ProduceNothing()
    {
        var t = new ProgressTracker();
        Assert.Null(t.Apply(Eph("""{"type":"system","subtype":"status","status":"requesting"}""")));
        Assert.Null(t.Apply(Eph("""{"type":"stream_event","event":{"type":"content_block_stop","index":0}}""")));
        Assert.Null(t.Apply(Eph(TextStart)));
    }
}

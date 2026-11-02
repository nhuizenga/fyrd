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

    private static string InputDelta(string partial) =>
        """{"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":""" + JsonSerializer.Serialize(partial) + "}}}";

    private const string BashStart = """{"type":"stream_event","event":{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","name":"Bash"}}}""";

    [Fact]
    public void ToolInput_StreamsTheBashCommandIntoTheDetail()
    {
        var t = new ProgressTracker();
        t.Apply(Eph(BashStart));
        Assert.Null(t.Apply(Eph(InputDelta("{\"comm"))));
        var item = t.Apply(Eph(InputDelta("and\": \"git status && ls \\\"my d")))!;
        Assert.Equal("Using Bash", item.Status);
        Assert.Equal("git status && ls \"my d", item.Detail);
        item = t.Apply(Eph(InputDelta("ir\\\"\", \"description\": \"Check\"}")))!;
        Assert.Equal("git status && ls \"my dir\"", item.Detail);

        // The heartbeat keeps the command while updating the status.
        item = t.Apply(Eph("""{"type":"tool_progress","tool_name":"Bash","elapsed_time_seconds":5}"""))!;
        Assert.Equal("Running Bash · 5s", item.Status);
        Assert.Equal("git status && ls \"my dir\"", item.Detail);
    }

    [Fact]
    public void ToolInput_ShowsTheGrepPatternNotItsPath()
    {
        var t = new ProgressTracker();
        t.Apply(Eph(ToolStart));
        Assert.Equal("TODO", t.Apply(Eph(InputDelta("{\"pattern\":\"TODO\",\"path\":\"src\"}")))!.Detail);
    }

    [Fact]
    public void ToolInput_StopsScanningOnceTheValueIsComplete()
    {
        var t = new ProgressTracker();
        t.Apply(Eph(BashStart));
        Assert.Equal("ls", t.Apply(Eph(InputDelta("{\"command\":\"ls\",")))!.Detail);
        // A huge trailing field (a heredoc, a Write's content) neither changes the detail nor produces more items.
        for (var i = 0; i < 50; i++)
            Assert.Null(t.Apply(Eph(InputDelta(i == 0 ? "\"description\":\"" + new string('x', 5000) : new string('y', 5000)))));
    }

    [Fact]
    public void ToolInput_ShowsPathsAndClearsWithTheTurn()
    {
        var t = new ProgressTracker();
        t.Apply(Eph(ToolStart));
        Assert.Equal("src/a.cs", t.Apply(Eph(InputDelta("""{"file_path":"src/a.cs","limit":10}""")))!.Detail);
        Assert.Null(t.Reset(At)!.Detail);
    }

    [Fact]
    public void ToolProgressHeartbeat_ShowsTheToolAndElapsedTime()
    {
        var t = new ProgressTracker();
        t.Apply(Eph(ToolStart));
        var item = t.Apply(Eph("""{"type":"tool_progress","tool_name":"Bash","elapsed_time_seconds":30,"heartbeat":true,"tool_use_id":"toolu_1-heartbeat-0"}"""))!;
        Assert.Equal("Running Bash · 30s", item.Status);
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

    [Fact]
    public void AgentToolUse_ShowsStartingAWorker_WithItsDescription()
    {
        var t = new ProgressTracker();
        var started = t.Apply(Eph("""{"type":"stream_event","event":{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","name":"Agent"}}}"""))!;
        Assert.Equal("Starting a worker", started.Status);
        var item = t.Apply(Eph("""{"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"subagent_type\":\"fork\",\"description\":\"build: initial task\"}"}}}"""))!;
        Assert.Equal("build: initial task", item.Detail);
    }

    private static StreamedEvent Saved(string payload)
    {
        var raw = JsonDocument.Parse(payload).RootElement.Clone();
        return new StreamedEvent(5, new SessionEvent("u", raw.GetProperty("type").GetString()!, At, null, raw));
    }

    private const string WorkerStarted = """{"type":"system","subtype":"task_started","task_type":"local_agent","description":"build: initial task","subagent_type":"fork","tool_use_id":"toolu_w1","task_id":"a1"}""";

    [Fact]
    public void RunningWorker_StaysOnTheLine_AfterAMainEventClearsIt_UntilItsNotification()
    {
        var t = new ProgressTracker();
        t.ApplySaved(Saved(WorkerStarted));
        var shown = t.Reset(At)!;
        Assert.Equal("Waiting for workers", shown.Status);
        var w = Assert.Single(shown.Workers!);
        Assert.Equal(new TranscriptWorker("toolu_w1", "build: initial task", null), w);

        // A later saved event with no change to the workers reports nothing new.
        t.ApplySaved(Saved("""{"type":"assistant","message":{"content":[]}}"""));
        Assert.Null(t.Reset(At));

        t.ApplySaved(Saved("""{"type":"system","subtype":"task_notification","status":"completed","tool_use_id":"toolu_w1","summary":"done"}"""));
        var cleared = t.Reset(At)!;
        Assert.Null(cleared.Workers);
        Assert.Null(cleared.Status);
    }

    [Fact]
    public void BashTask_IsNotAWorker()
    {
        var t = new ProgressTracker();
        t.ApplySaved(Saved("""{"type":"system","subtype":"task_started","task_type":"local_bash","description":"ls","tool_use_id":"toolu_b"}"""));
        Assert.Null(t.Reset(At));
    }

    [Fact]
    public void WorkerHeartbeatAndToolStart_SetThatWorkersLine_NotTheMainStatus()
    {
        var t = new ProgressTracker();
        t.ApplySaved(Saved(WorkerStarted));
        t.Reset(At);
        var started = t.Apply(Eph("""{"type":"stream_event","parent_tool_use_id":"toolu_w1","event":{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","name":"Edit"}}}"""))!;
        Assert.Equal("Using Edit", started.Workers![0].Line);
        var beat = t.Apply(Eph("""{"type":"tool_progress","parent_tool_use_id":"toolu_w1","tool_name":"Bash","elapsed_time_seconds":42}"""))!;
        Assert.Equal("Running Bash · 42s", beat.Workers![0].Line);
        Assert.Equal("Waiting for workers", beat.Status);
        // The same line again changes nothing; a heartbeat for the main agent's own call still sets the main status.
        Assert.Null(t.Apply(Eph("""{"type":"tool_progress","parent_tool_use_id":"toolu_w1","tool_name":"Bash","elapsed_time_seconds":42}""")));
        var main = t.Apply(Eph("""{"type":"tool_progress","parent_tool_use_id":"toolu_main_bash","tool_name":"Bash","elapsed_time_seconds":90}"""))!;
        Assert.Equal("Running Bash · 90s", main.Status);
        Assert.Equal("Running Bash · 42s", main.Workers![0].Line);
    }

    [Fact]
    public void UnknownSubagentStreamEvent_IsIgnored()
    {
        var t = new ProgressTracker();
        Assert.Null(t.Apply(Eph("""{"type":"stream_event","parent_tool_use_id":"toolu_x","event":{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","name":"Edit"}}}""")));
    }
}

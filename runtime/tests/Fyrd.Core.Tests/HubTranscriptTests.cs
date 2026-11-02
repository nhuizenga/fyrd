using System.Text.Json;
using Fyrd.Core.CloudSessions;
using Fyrd.Core.Hub;

namespace Fyrd.Core.Tests;

public class HubTranscriptTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static StreamedEvent Event(long seq, string payload)
    {
        var raw = JsonDocument.Parse(payload).RootElement.Clone();
        var type = raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()! : "";
        return new StreamedEvent(seq, new SessionEvent("u" + seq, type, At, null, raw));
    }

    [Fact]
    public void Assistant_TextBlocks_OneAgentTextItem()
    {
        var items = HubTranscript.Map(Event(7, """
            {"type":"assistant","message":{"content":[{"type":"text","text":"Hello "},{"type":"thinking","thinking":"x"},{"type":"text","text":"world"}]}}
            """));
        var item = Assert.Single(items);
        Assert.Equal(new TranscriptItem("7:0", 7, "agent", "text", "Hello world", At), item);
    }

    [Fact]
    public void Assistant_BlankTextAndOtherTools_NoItems()
    {
        var items = HubTranscript.Map(Event(3, """
            {"type":"assistant","message":{"content":[{"type":"text","text":"  "},{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"ls"}}]}}
            """));
        Assert.Empty(items);
    }

    [Fact]
    public void Assistant_AskUserQuestion_QuestionItemAfterText()
    {
        var items = HubTranscript.Map(Event(9, """
            {"type":"assistant","message":{"content":[
              {"type":"text","text":"Two things."},
              {"type":"tool_use","id":"toolu_9","name":"AskUserQuestion","input":{"questions":[
                {"question":"Pick a fruit","header":"Fruit","multiSelect":false,"options":[{"label":"Apple","description":"Red"},{"label":"Banana"}]},
                {"question":"Pick colours","multiSelect":true,"options":[{"label":"Red"}]}]}}]}}
            """));
        Assert.Equal(2, items.Count);
        Assert.Equal(("9:0", "text"), (items[0].Key, items[0].Kind));

        var q = items[1];
        Assert.Equal("9:1", q.Key);
        Assert.Equal(("agent", "question"), (q.Role, q.Kind));
        Assert.Equal("Pick a fruit\nPick colours", q.Text);
        Assert.Equal("toolu_9", q.ToolUseId);
        Assert.Equal(2, q.Questions!.Count);
        Assert.Equal("Fruit", q.Questions[0].Header);
        Assert.False(q.Questions[0].MultiSelect);
        Assert.Equal([new TranscriptOption("Apple", "Red"), new TranscriptOption("Banana", null)], q.Questions[0].Options);
        Assert.Null(q.Questions[1].Header);
        Assert.True(q.Questions[1].MultiSelect);
    }

    [Fact]
    public void User_StringContent_UserTextItem()
    {
        var item = Assert.Single(HubTranscript.Map(Event(2, """{"type":"user","parent_tool_use_id":null,"message":{"role":"user","content":"Build it"}}""")));
        Assert.Equal(new TranscriptItem("2:0", 2, "user", "text", "Build it", At), item);
    }

    [Fact]
    public void User_TextBlocks_JoinedAndToolResultsIgnored()
    {
        var items = HubTranscript.Map(Event(4, """
            {"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"out"},{"type":"text","text":"Also this"}]}}
            """));
        Assert.Equal("Also this", Assert.Single(items).Text);
    }

    [Fact]
    public void User_OnlyToolResults_NoItems() =>
        Assert.Empty(HubTranscript.Map(Event(5, """
            {"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"out"}]}}
            """)));

    [Fact]
    public void User_SubagentPrompt_Skipped() =>
        Assert.Empty(HubTranscript.Map(Event(6, """{"type":"user","parent_tool_use_id":"toolu_task","message":{"content":"Explore the repo"}}""")));

    [Fact]
    public void ControlResponse_AskUserQuestionAnswers_UserAnswerItem()
    {
        var item = Assert.Single(HubTranscript.Map(Event(11, """
            {"type":"control_response","response":{"subtype":"success","request_id":"req_1","response":{
              "behavior":"allow","toolUseID":"toolu_9","tool_name":"AskUserQuestion",
              "updatedInput":{"questions":[],"answers":{"Pick a fruit":"Apple","Pick colours":"Red"}}}}}
            """)));
        Assert.Equal(new TranscriptItem("11:0", 11, "user", "answer", "Pick a fruit → Apple\nPick colours → Red", At, null, "toolu_9"), item);
    }

    [Fact]
    public void ControlResponse_OtherTool_NoItems() =>
        Assert.Empty(HubTranscript.Map(Event(12, """
            {"type":"control_response","response":{"subtype":"success","response":{"tool_name":"Bash","updatedInput":{"answers":{"a":"b"}}}}}
            """)));

    [Theory]
    [InlineData("""{"type":"result","subtype":"success"}""")]
    [InlineData("""{"type":"system","subtype":"init"}""")]
    [InlineData("""{"type":"assistant","message":"not an object"}""")]
    [InlineData("""{"type":"assistant","message":{"content":[1,null,{"type":"tool_use","name":"AskUserQuestion","input":{"questions":"nope"}}]}}""")]
    [InlineData("""{"type":"user","message":{"content":42}}""")]
    [InlineData("""{"type":"control_response","response":{"response":{"tool_name":"AskUserQuestion","updatedInput":{"answers":[]}}}}""")]
    [InlineData("""[1,2,3]""")]
    [InlineData("""null""")]
    public void OtherOrMalformedPayloads_NoItemsNoThrow(string payload) =>
        Assert.Empty(HubTranscript.Map(Event(1, payload)));

    private static IReadOnlyList<TranscriptItem> MapFixture(string name, ISet<string> ids) =>
        FixtureEvents.Load(name).SelectMany((ev, i) => HubTranscript.Map(new StreamedEvent(100 + i, ev), ids)).ToList();

    [Fact]
    public void RealForkEvents_WorkerStartedThenResultFromTaskNotification_AndNothingElse()
    {
        // Captured from a cloud session: background_tasks_changed, task_started (local_agent), the "launched" tool_result,
        // the fork's own assistant message (parent_tool_use_id set), task_updated, task_notification, background_tasks_changed.
        var ids = new HashSet<string>();
        var items = MapFixture("fork-worker-events.json", ids);
        Assert.Equal(["worker", "worker-result"], items.Select(i => i.Kind));
        Assert.Equal("Fork of this session", items[0].Text);
        Assert.Equal("fork", items[0].Detail);
        Assert.Equal("toolu_011Z6kMxGHdjM8Pymjn3LFcC", items[0].ToolUseId);
        Assert.Equal(items[0].ToolUseId, items[1].ToolUseId);
        Assert.Equal("completed", items[1].Status);
        Assert.StartsWith("Task: acknowledge the fork", items[1].Text);
    }

    [Fact]
    public void BashTaskNotification_IsNotAWorkerResult()
    {
        var items = HubTranscript.Map(Event(20, """
            {"type":"system","subtype":"task_notification","status":"completed","summary":"ls","task_id":"b1","tool_use_id":"toolu_bash"}
            """), new HashSet<string>());
        Assert.Empty(items);
        Assert.Empty(HubTranscript.Map(Event(21, """
            {"type":"system","subtype":"task_started","task_type":"local_bash","description":"ls","task_id":"b1","tool_use_id":"toolu_bash"}
            """), new HashSet<string>()));
    }

    [Fact]
    public void Assistant_WithParentToolUse_IsAWorkersMessage_NotTheAgents()
    {
        Assert.Empty(HubTranscript.Map(Event(22, """
            {"type":"assistant","parent_tool_use_id":"toolu_w1","agent_id":"a1","message":{"content":[{"type":"text","text":"worker chatter"}]}}
            """)));
    }
}

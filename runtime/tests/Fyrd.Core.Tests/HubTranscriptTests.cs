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
}

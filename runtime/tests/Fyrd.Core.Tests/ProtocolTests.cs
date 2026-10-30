using System.Text.Json;
using Fyrd.Core.Orchestration;

namespace Fyrd.Core.Tests;

public class ProtocolTests
{
    private const string F = "```";

    [Fact]
    public void Parse_QuestionBlock_FromSpikeShape()
    {
        var text = "I ran a tool before this reply.\n\n" + F + "fyrd-question\n{\"id\":\"q-color\",\"question\":\"Which colour?\",\"options\":[\"red\",\"blue\"]}\n" + F;
        var r = Protocol.Parse(text);
        var q = Assert.Single(r.Questions);
        Assert.Equal("q-color", q.Id);
        Assert.Equal(new[] { "red", "blue" }, q.Options);
        Assert.Null(q.Recommendation);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void Parse_QuestionBlock_AllFieldsAndIndentedLongFence()
    {
        var text = "  ````fyrd-question\n  {\"id\":\"q1\",\"question\":\"Q?\",\"recommendation\":\"a\",\"reason\":\"r\",\"unit\":null,\"details\":\"d\"}\n  ````";
        var q = Assert.Single(Protocol.Parse(text).Questions);
        Assert.Empty(q.Options);
        Assert.Equal("a", q.Recommendation);
        Assert.Equal("r", q.Reason);
        Assert.Null(q.Unit);
        Assert.Equal("d", q.Details);
    }

    [Fact]
    public void Parse_UnitsBlock_LastOneWins()
    {
        var text = F + "fyrd-units\n[{\"id\":\"a\",\"title\":\"A\",\"done\":false}]\n" + F + "\ntext\n" +
                   F + "fyrd-units\n[{\"id\":\"a\",\"title\":\"A\",\"done\":true},{\"id\":\"b\",\"title\":\"B\"}]\n" + F;
        var u = Protocol.Parse(text).Units;
        Assert.NotNull(u);
        Assert.Equal(2, u!.Count);
        Assert.True(u[0].Done);
        Assert.False(u[1].Done);
    }

    [Fact]
    public void Parse_DoneAndFailed()
    {
        var text = F + "fyrd-done\n{\"branch\":\"fyrd/s1\",\"summary\":\"ok\"}\n" + F + "\n" + F + "fyrd-failed\n{\"reason\":\"boom\"}\n" + F;
        var r = Protocol.Parse(text);
        Assert.Equal("fyrd/s1", r.Done!.Branch);
        Assert.Equal("ok", r.Done.Summary);
        Assert.Equal("boom", r.Failed!.Reason);
    }

    [Fact]
    public void Parse_SkipsMalformedBlock_KeepsOthers()
    {
        var text = F + "fyrd-question\n{nope\n" + F + "\n" + F + "fyrd-units\n[{\"id\":\"a\",\"title\":\"A\",\"done\":false}]\n" + F;
        var r = Protocol.Parse(text);
        Assert.Equal(1, r.Units!.Count);
        Assert.Empty(r.Questions);
        Assert.Single(r.Warnings);
    }

    [Fact]
    public void Parse_WrongShapeNeverThrows()
    {
        var text = F + "fyrd-units\n{\"a\":1}\n" + F + "\n" + F + "fyrd-done\n[1]\n" + F + "\n" + F + "fyrd-question\n{\"id\":5}\n" + F;
        var r = Protocol.Parse(text);
        Assert.Null(r.Units);
        Assert.Null(r.Done);
        Assert.Empty(r.Questions);
        Assert.Equal(3, r.Warnings.Count);
    }

    [Fact]
    public void Parse_RejectsBadUnitId()
    {
        var text = F + "fyrd-units\n[{\"id\":\"../x\",\"title\":\"X\",\"done\":false},{\"id\":\"ok-1\",\"title\":\"Y\",\"done\":false}]\n" + F;
        var r = Protocol.Parse(text);
        Assert.Equal("ok-1", Assert.Single(r.Units!).Id);
        Assert.Single(r.Warnings);
    }

    [Fact]
    public void Parse_IgnoresOtherFences()
    {
        var r = Protocol.Parse(F + "json\n{\"id\":\"x\"}\n" + F);
        Assert.Null(r.Units);
        Assert.Empty(r.Questions);
        Assert.Null(r.Done);
        Assert.Null(r.Failed);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void FormatAnswer_RoundTripsThroughJson()
    {
        var s = Protocol.FormatAnswer("q1", "say \"hi\"\nline2");
        var lines = s.Split('\n');
        Assert.Equal(F + "fyrd-answer", lines[0].TrimEnd());
        Assert.Equal(F, lines[^1].TrimEnd());
        var body = string.Join("\n", lines[1..^1]);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("q1", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("say \"hi\"\nline2", doc.RootElement.GetProperty("answer").GetString());
    }

    [Fact]
    public void Parse_CrlfText_ParsesAllBlocks()
    {
        var text = F + "fyrd-units\r\n[{\"id\":\"a\",\"title\":\"A\"}]\r\n" + F + "\r\n\r\n"
            + F + "fyrd-question\r\n{\"id\":\"q1\",\"question\":\"Why?\"}\r\n" + F;
        var r = Protocol.Parse(text);
        Assert.Single(r.Units!);
        Assert.Single(r.Questions);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void FormatConfig_EmitsSquadCap()
    {
        var s = Protocol.FormatConfig(3);
        Assert.Contains("fyrd-config", s);
        Assert.Contains("{\"squadCap\":3}", s);
    }
}

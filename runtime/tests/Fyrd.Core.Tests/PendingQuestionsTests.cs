using Fyrd.Core.Hub;

namespace Fyrd.Core.Tests;

public sealed class PendingQuestionsTests
{
    [Fact]
    public void Find_PendingFixture_OneQuestion()
    {
        var events = FixtureEvents.Load("ask-user-pending.json");

        var q = Assert.Single(PendingQuestions.Find("s1", "builder-ab12", events));

        Assert.Equal("Pick a fruit", q.Question);
        Assert.Equal(["Apple", "Banana"], q.Options);
        Assert.False(q.MultiSelect);
        Assert.Equal("s1", q.SessionId);
        Assert.Equal("builder-ab12", q.SessionTitle);
        Assert.Equal("toolu_01Amojjvp8qbuPwgLVyQZNex", q.ToolUseId);
        Assert.Equal("bda7e33e-50a0-41aa-bc1b-4cd5c14452da", q.RequestId);
        Assert.Equal("s1:bda7e33e-50a0-41aa-bc1b-4cd5c14452da:0", q.Id);
        Assert.Equal(DateTimeOffset.Parse("2026-10-31T20:54:54.124225Z"), q.AskedAt);
        Assert.Equal("Pick a fruit", q.Input.GetProperty("questions")[0].GetProperty("question").GetString());
    }

    [Fact]
    public void Find_AnsweredFixture_None()
    {
        var events = FixtureEvents.Load("ask-user-answered.json");

        Assert.Empty(PendingQuestions.Find("s1", "builder-ab12", events));
    }

    [Fact]
    public void Find_NoEvents_None() => Assert.Empty(PendingQuestions.Find("s1", "t", []));
}

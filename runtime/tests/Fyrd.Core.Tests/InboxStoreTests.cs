using Fyrd.Core.Specs;
namespace Fyrd.Core.Tests;

public class InboxStoreTests
{
    private const string Repo = "servicetitan/titan-lens-ossie";
    private const string Question = "What kind of test coverage do you expect?";
    private static readonly DateTimeOffset T = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    private static (InboxStore Inbox, TempHome Home, string SpecId) Make()
    {
        var home = new TempHome();
        var time = new FixedTime(T);
        var specs = new SpecStore(home.Paths, time);
        var id = specs.Create("Add export button", Repo, "x").Id;
        return (new InboxStore(home.Paths, specs, new DecisionMemory(home.Paths), time), home, id);
    }

    private static InboxCard AddCard(InboxStore s, string specId) =>
        s.Add(specId, "u1", Question, ["Unit", "E2E"], "Unit", "cheap", "details", true);

    private static string RepoFile(TempHome h) =>
        Path.Combine(h.Paths.MemoryDir, "servicetitan__titan-lens-ossie.md");

    private static int QuestionCount(string text) => text.Split("**Q:**").Length - 1;

    [Fact]
    public void ListOpen_SkipsCorruptCard()
    {
        var (s, home, spec) = Make();
        using var _ = home;
        var good = AddCard(s, spec);
        File.WriteAllText(Path.Combine(home.Paths.InboxDir, "badbadbadbad.json"), "{ nope");
        Assert.Equal(good.Id, Assert.Single(s.ListOpen()).Id);
    }

    [Fact]
    public void Add_ThenListOpen_ReturnsCard()
    {
        var (s, home, spec) = Make();
        using var _ = home;
        var added = AddCard(s, spec);

        var open = Assert.Single(s.ListOpen());
        Assert.Equal(added.Id, open.Id);
        Assert.Equal(12, open.Id.Length);
        Assert.Equal(["Unit", "E2E"], open.Options);
        Assert.Equal("Unit", open.Recommendation);
        Assert.Equal("u1", open.UnitId);
        Assert.True(open.AllowFreeText);
    }

    [Fact]
    public void Answer_RemembersByDefault_InRepoFile()
    {
        var (s, home, spec) = Make();
        using var _ = home;
        var card = AddCard(s, spec);

        var answered = s.Answer(card.Id, "Unit tests only");

        Assert.Equal("Unit tests only", answered.Answer!.Text);
        var text = File.ReadAllText(RepoFile(home));
        Assert.Contains("**Q:** " + Question, text);
        Assert.Contains("**A:** Unit tests only", text);
        Assert.False(File.Exists(Path.Combine(home.Paths.MemoryDir, "global.md")));
    }

    [Fact]
    public void Answer_GlobalScope_WritesGlobalFile()
    {
        var (s, home, spec) = Make();
        using var _ = home;
        var card = AddCard(s, spec);

        s.Answer(card.Id, "Unit tests only", scope: MemoryScope.Global);

        Assert.Contains("**A:** Unit tests only", File.ReadAllText(Path.Combine(home.Paths.MemoryDir, "global.md")));
        Assert.False(File.Exists(RepoFile(home)));
    }

    [Fact]
    public void Answer_RememberFalse_WritesNoMemory()
    {
        var (s, home, spec) = Make();
        using var _ = home;
        var card = AddCard(s, spec);

        s.Answer(card.Id, "Unit tests only", remember: false);

        Assert.Empty(Directory.GetFiles(home.Paths.MemoryDir));
    }

    [Fact]
    public void Answer_RemovesFromListOpen()
    {
        var (s, home, spec) = Make();
        using var _ = home;
        var card = AddCard(s, spec);
        s.Answer(card.Id, "x");
        Assert.Empty(s.ListOpen());
    }

    [Fact]
    public void Answer_Twice_Throws_AndRecordsMemoryOnce()
    {
        var (s, home, spec) = Make();
        using var _ = home;
        var card = AddCard(s, spec);
        s.Answer(card.Id, "first");

        Assert.Throws<CardAlreadyAnsweredException>(() => s.Answer(card.Id, "second"));

        var text = File.ReadAllText(RepoFile(home));
        Assert.Equal(1, QuestionCount(text));
        Assert.DoesNotContain("second", text);
    }

    [Fact]
    public void Answer_Concurrent_OnlyOneWins()
    {
        var (s, home, spec) = Make();
        using var _ = home;
        var card = AddCard(s, spec);
        var wins = 0;
        Parallel.For(0, 8, i =>
        {
            try { s.Answer(card.Id, "a" + i); Interlocked.Increment(ref wins); }
            catch (CardAlreadyAnsweredException) { }
        });
        Assert.Equal(1, wins);
        Assert.Equal(1, QuestionCount(File.ReadAllText(RepoFile(home))));
    }

    [Fact]
    public void Answer_UnknownCard_ThrowsKeyNotFound()
    {
        var (s, home, _) = Make();
        using var _h = home;
        Assert.Throws<KeyNotFoundException>(() => s.Answer("abcdef123456", "x"));
    }

    [Fact]
    public void Answer_BadInput_ThrowsArgument()
    {
        var (s, home, spec) = Make();
        using var _ = home;
        var card = AddCard(s, spec);
        Assert.Throws<ArgumentException>(() => s.Answer(card.Id, "  "));
        Assert.Throws<ArgumentException>(() => s.Answer("../x", "ok"));
    }

    [Fact]
    public void Inbox_HasSourceKey_TrueAfterAnswer()
    {
        var (s, home, spec) = Make();
        using var _ = home;
        Assert.False(s.HasSourceKey("k1"));
        var card = s.Add(spec, null, Question, [], null, null, null, true, "k1");
        Assert.True(s.HasSourceKey("k1"));
        s.Answer(card.Id, "yes", remember: false);
        Assert.True(s.HasSourceKey("k1"));
        Assert.False(s.HasSourceKey("k2"));
    }

    [Fact]
    public void ListOpenFor_FiltersBySpec()
    {
        var (s, home, spec) = Make();
        using var _ = home;
        var mine = AddCard(s, spec);
        Assert.Equal(mine.Id, Assert.Single(s.ListOpenFor(spec)).Id);
        Assert.Empty(s.ListOpenFor("other-spec"));
    }

    [Fact]
    public void Answer_AttentionCard_RecordsNoMemory()
    {
        var (s, home, spec) = Make();
        using var _ = home;
        var card = s.Add(spec, null, "Lead needs attention: Approve Bash", [], null, null, null, true,
            spec + ":attention:e1");

        var answered = s.Answer(card.Id, "go ahead", remember: true);

        Assert.False(answered.Answer!.Remember);
        Assert.False(File.Exists(RepoFile(home)));
        Assert.Empty(Directory.GetFiles(home.Paths.MemoryDir));
    }

    private static InboxCard AnsweredKeyed(InboxStore s, string specId)
    {
        var c = s.Add(specId, null, Question, [], null, null, null, true, specId + ":q1");
        return s.Answer(c.Id, "Unit", remember: false);
    }

    [Fact]
    public void SetRelay_FailedIncrementsAttempts()
    {
        var (s, home, spec) = Make();
        using var _ = home;
        var c = AnsweredKeyed(s, spec);
        s.SetRelay(c.Id, "pending", null);
        var f = s.SetRelay(c.Id, "failed", "boom").Answer!;
        Assert.Equal(("failed", 1, "boom"), (f.RelayStatus, f.RelayAttempts, f.RelayError));
        var sent = s.SetRelay(c.Id, "sent", null).Answer!;
        Assert.Equal(("sent", 1, null), (sent.RelayStatus, sent.RelayAttempts, sent.RelayError));
    }

    [Fact]
    public void ListRelaysToRetry_StopsAtFive()
    {
        var (s, home, spec) = Make();
        using var _ = home;
        var c = AnsweredKeyed(s, spec);
        for (var i = 0; i < 4; i++) s.SetRelay(c.Id, "failed", "x");
        Assert.Single(s.ListRelaysToRetry());
        s.SetRelay(c.Id, "failed", "x");
        Assert.Empty(s.ListRelaysToRetry());
        s.ResetRelay(c.Id);
        var again = Assert.Single(s.ListRelaysToRetry());
        Assert.Equal(("pending", 0), (again.Answer!.RelayStatus, again.Answer.RelayAttempts));
    }
}

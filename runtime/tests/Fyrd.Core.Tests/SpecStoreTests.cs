using Fyrd.Core.Specs;

namespace Fyrd.Core.Tests;

public class SpecStoreTests
{
    private static SpecStore Store(TempHome h, string date) =>
        new(h.Paths, new FixedTime(DateTimeOffset.Parse(date + "T12:00:00Z")));

    private const string Repo = "servicetitan/titan-lens-ossie";

    [Fact]
    public void Create_WritesHandEditableFiles()
    {
        using var h = new TempHome(); var s = Store(h, "2026-10-29");
        var run = s.Create("Add export button", Repo, "# Export\nDo it.");
        Assert.Equal("20261029-add-export-button", run.Id);
        Assert.Equal(SpecStatus.Draft, run.Status);
        Assert.Equal("# Export\nDo it.", File.ReadAllText(Path.Combine(h.Paths.SpecDir(run.Id), "spec.md")));
        Assert.Equal("[]", File.ReadAllText(Path.Combine(h.Paths.SpecDir(run.Id), "units.json")).Trim());
        Assert.Equal("# Export\nDo it.", s.ReadMarkdown(run.Id));
        Assert.Empty(s.ListUnits(run.Id));
    }

    [Fact]
    public void Create_SameTitleTwice_GetsSuffix()
    {
        using var h = new TempHome(); var s = Store(h, "2026-10-29");
        s.Create("Add export button", Repo, "x");
        var second = s.Create("Add export button", Repo, "x");
        var third = s.Create("Add export button", Repo, "x");
        Assert.Equal("20261029-add-export-button-2", second.Id);
        Assert.Equal("20261029-add-export-button-3", third.Id);
    }

    [Fact]
    public void Create_SlugifiesUnsafeTitle()
    {
        using var h = new TempHome(); var s = Store(h, "2026-10-29");
        var run = s.Create("../../etc/Passwd ✨ now", Repo, "x");
        Assert.Equal("20261029-etc-passwd-now", run.Id);
        var dir = Path.GetFullPath(h.Paths.SpecDir(run.Id));
        Assert.StartsWith(h.Paths.SpecsDir, dir);
        Assert.Equal(h.Paths.SpecsDir, Path.GetDirectoryName(dir));
    }

    [Fact]
    public void Create_EmptyTitleAndLongTitle_StayValid()
    {
        using var h = new TempHome(); var s = Store(h, "2026-10-29");
        Assert.Equal("20261029-spec", s.Create("✨✨", Repo, "x").Id);
        var longId = s.Create(new string('a', 100), Repo, "x").Id;
        Assert.Equal(9 + 40, longId.Length);
        Assert.True(SpecIds.IsValid(longId));
    }

    [Fact]
    public void Create_EmptyMarkdown_Throws()
    {
        using var h = new TempHome(); var s = Store(h, "2026-10-29");
        Assert.Throws<ArgumentException>(() => s.Create("t", Repo, "  "));
    }

    [Fact]
    public void Create_BadRepo_Throws()
    {
        using var h = new TempHome(); var s = Store(h, "2026-10-29");
        Assert.Throws<ArgumentException>(() => s.Create("t", "not a repo", "x"));
    }

    [Fact]
    public void Get_RejectsTraversalId()
    {
        using var h = new TempHome(); var s = Store(h, "2026-10-29");
        Assert.Throws<ArgumentException>(() => s.Get("..\\..\\x"));
        Assert.Throws<ArgumentException>(() => s.Get("../x"));
    }

    [Fact]
    public void List_SkipsMalformedRun_AndReportsIt()
    {
        using var h = new TempHome(); var s = Store(h, "2026-10-29");
        var a = s.Create("First", Repo, "x");
        var b = s.Create("Second", Repo, "x");
        File.WriteAllText(Path.Combine(h.Paths.SpecDir(b.Id), "run.json"), "{nope");
        var list = s.List();
        Assert.Equal(2, list.Count);
        Assert.Single(list, e => e.Run != null && e.Id == a.Id);
        var bad = Assert.Single(list, e => e.Run == null);
        Assert.Equal(b.Id, bad.Id);
        Assert.Contains("run.json", bad.Error);
    }

    [Fact]
    public void List_NewestFirst()
    {
        using var h = new TempHome();
        var old = Store(h, "2026-10-28").Create("Old", Repo, "x");
        var recent = Store(h, "2026-10-29").Create("New", Repo, "x");
        Assert.Equal([recent.Id, old.Id], Store(h, "2026-10-30").List().Select(e => e.Id));
    }

    [Fact]
    public void UpdateStatus_Persists()
    {
        using var h = new TempHome();
        var run = Store(h, "2026-10-29").Create("t", Repo, "x");
        Store(h, "2026-10-29").UpdateStatus(run.Id, SpecStatus.Queued);
        Assert.Equal(SpecStatus.Queued, Store(h, "2026-10-29").Get(run.Id)!.Status);
    }

    [Fact]
    public void HandEditedTitle_IsRead()
    {
        using var h = new TempHome(); var s = Store(h, "2026-10-29");
        var run = s.Create("Original", Repo, "x");
        var path = Path.Combine(h.Paths.SpecDir(run.Id), "run.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("Original", "Edited by hand"));
        Assert.Equal("Edited by hand", s.Get(run.Id)!.Title);
    }

    [Fact]
    public void TryTransition_OnlyFromExpectedStatus()
    {
        using var h = new TempHome(); var s = Store(h, "2026-10-29");
        var id = s.Create("T", Repo, "x").Id;
        Assert.True(s.TryTransition(id, SpecStatus.Draft, SpecStatus.Queued));
        Assert.False(s.TryTransition(id, SpecStatus.Draft, SpecStatus.Queued));
        Assert.Equal(SpecStatus.Queued, s.Get(id)!.Status);
    }

    [Fact]
    public void TryTransition_Concurrent_OnlyOneWins()
    {
        using var h = new TempHome(); var s = Store(h, "2026-10-29");
        var id = s.Create("T", Repo, "x").Id;
        var wins = 0;
        Parallel.For(0, 8, _ => { if (s.TryTransition(id, SpecStatus.Draft, SpecStatus.Queued)) Interlocked.Increment(ref wins); });
        Assert.Equal(1, wins);
    }

    [Fact]
    public void SetLead_SetsSessionAndBuilding()
    {
        using var h = new TempHome(); var s = Store(h, "2026-10-29");
        var id = s.Create("T", Repo, "x").Id;
        var run = s.SetLead(id, "session_1");
        Assert.Equal("session_1", run.SessionId); Assert.Equal(SpecStatus.Building, run.Status);
        Assert.Equal("session_1", s.Get(id)!.SessionId);
    }

    [Fact]
    public void SetCursor_And_Finish_Persist()
    {
        using var h = new TempHome(); var s = Store(h, "2026-10-29");
        var id = s.Create("T", Repo, "x").Id;
        s.SetCursor(id, "evt_9");
        var done = s.Finish(id, SpecStatus.Failed, "fyrd/x", "boom");
        var got = s.Get(id)!;
        Assert.Equal("evt_9", got.LastEventId); Assert.Equal(SpecStatus.Failed, got.Status);
        Assert.Equal("fyrd/x", got.Branch); Assert.Equal("boom", got.Error); Assert.Equal(done, got);
    }

    [Fact]
    public void WriteUnits_IsHandEditableJson()
    {
        using var h = new TempHome(); var s = Store(h, "2026-10-29");
        var id = s.Create("T", Repo, "x").Id;
        s.WriteUnits(id, [new Unit("u1", id, "Do it", UnitStage.Implement, UnitState.Running)]);
        Assert.Contains("\"stage\": \"Implement\"", File.ReadAllText(Path.Combine(h.Paths.SpecDir(id), "units.json")));
        Assert.Equal("u1", Assert.Single(s.ListUnits(id)).Id);
    }

    [Fact]
    public void ListActive_SkipsDraftDoneAndMalformed()
    {
        using var h = new TempHome(); var s = Store(h, "2026-10-29");
        var draft = s.Create("Draft", Repo, "x").Id;
        var queued = s.Create("Queued", Repo, "x").Id; s.UpdateStatus(queued, SpecStatus.Queued);
        var building = s.Create("Building", Repo, "x").Id; s.UpdateStatus(building, SpecStatus.Building);
        var review = s.Create("Review", Repo, "x").Id; s.UpdateStatus(review, SpecStatus.FinalReview);
        var done = s.Create("Done", Repo, "x").Id; s.UpdateStatus(done, SpecStatus.ReadyForHandoff);
        var bad = s.Create("Bad", Repo, "x").Id; File.WriteAllText(Path.Combine(h.Paths.SpecDir(bad), "run.json"), "{ nope");
        var ids = s.ListActive().Select(r => r.Id).ToHashSet();
        Assert.Equal(new HashSet<string> { queued, building, review }, ids);
    }

    [Fact]
    public void List_InvalidFolderName_IsErrorEntry()
    {
        using var h = new TempHome(); var s = Store(h, "2026-10-29");
        Directory.CreateDirectory(Path.Combine(h.Paths.SpecsDir, "archive"));
        var entry = Assert.Single(s.List());
        Assert.Null(entry.Run);
        Assert.Equal("archive", entry.Id);
        Assert.Equal("Not a valid spec folder name", entry.Error);
    }

    [Fact]
    public void ConcurrentReadersAndWriters_NeverThrow()
    {
        using var h = new TempHome(); var s = Store(h, "2026-10-29");
        var id = s.Create("Race", Repo, "x").Id;
        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        void Guard(Action a) { try { for (var i = 0; i < 200; i++) a(); } catch (Exception e) { errors.Enqueue(e); } }
        var tasks = new[]
        {
            Task.Run(() => Guard(() => s.UpdateStatus(id, SpecStatus.Draft))),
            Task.Run(() => Guard(() => s.WriteUnits(id, [new Unit("a", id, "A", UnitStage.Implement, UnitState.Idle)]))),
            Task.Run(() => Guard(() => { s.List(); s.Get(id); s.ListUnits(id); s.ReadMarkdown(id); })),
            Task.Run(() => Guard(() => { s.List(); s.Get(id); s.ListUnits(id); s.ReadMarkdown(id); })),
        };
        Task.WaitAll(tasks);
        Assert.Empty(errors);
    }
}

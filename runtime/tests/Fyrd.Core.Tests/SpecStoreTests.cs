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
}

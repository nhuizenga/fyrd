using Fyrd.Core.Config;
using Fyrd.Core.Hub;

namespace Fyrd.Core.Tests;

public sealed class SessionSkillsTests : IDisposable
{
    private readonly TempHome _home = new();
    private readonly string _seeds = Path.Combine(Path.GetTempPath(), "fyrd-skill-seeds-" + Guid.NewGuid());

    public SessionSkillsTests()
    {
        Seed("fyrd-stacks", "Stacks: use for a stack.", "builder", "# Stacks\n\nStack rules.");
        Seed("fyrd-events", "Events: use when an event arrives.", "builder, monitor", "# Events\n\nEvent rules.");
    }

    public void Dispose()
    {
        _home.Dispose();
        if (Directory.Exists(_seeds)) Directory.Delete(_seeds, true);
    }

    private SessionSkills Make() => new(_home.Paths, _seeds);

    private void Seed(string name, string description, string roles, string body, string? nameInFile = null)
    {
        Directory.CreateDirectory(Path.Combine(_seeds, name));
        File.WriteAllText(Path.Combine(_seeds, name, "SKILL.md"), $"---\nname: {nameInFile ?? name}\ndescription: {description}\nroles: {roles}\n---\n{body}\n");
    }

    private static SessionSkill Skill(string name = "fyrd-mine", string description = "Mine: use it.", string body = "Body.", params string[] roles) =>
        new(name, description, roles.Length == 0 ? ["builder"] : roles, body);

    [Fact]
    public void List_SeedsOnce_OrderedByName_WithRolesAndSeedHash()
    {
        var list = Make().List();
        Assert.Equal(["fyrd-events", "fyrd-stacks"], list.Select(s => s.Name));
        Assert.Equal(["builder", "monitor"], list[0].Roles);
        Assert.Equal("Events: use when an event arrives.", list[0].Description);
        Assert.Equal("# Stacks\n\nStack rules.", list[1].Body);
        Assert.All(list, s =>
        {
            Assert.Equal(SessionSkills.Hash(s), s.SeedHash);
            Assert.False(s.UpdateAvailable);
        });
        Assert.True(File.Exists(Path.Combine(_home.Paths.HubSessionSkillsDir, "fyrd-stacks.json")));
    }

    [Fact]
    public void UneditedSkill_FollowsItsSeed()
    {
        Make().List();
        Seed("fyrd-stacks", "Stacks: use for a stack.", "builder", "# Stacks\n\nBetter stack rules.");
        var after = Make().Get("fyrd-stacks")!;
        Assert.Equal("# Stacks\n\nBetter stack rules.", after.Body);
        Assert.Equal(SessionSkills.Hash(after), after.SeedHash);
        Assert.False(after.UpdateAvailable);
    }

    [Fact]
    public void EditedSkill_IsLeftAlone_AndFlagged_ThenResetRestoresTheSeed()
    {
        var store = Make();
        store.Save(store.Get("fyrd-stacks")! with { Body = "Mine." });
        Assert.False(Make().Get("fyrd-stacks")!.UpdateAvailable); // the seed it came from hasn't changed
        Seed("fyrd-stacks", "Stacks: use for a stack.", "builder", "# Stacks\n\nBetter stack rules.");
        var again = Make();
        var edited = again.Get("fyrd-stacks")!;
        Assert.Equal("Mine.", edited.Body);
        Assert.True(edited.UpdateAvailable);
        var reset = again.Reset("fyrd-stacks");
        Assert.Equal("# Stacks\n\nBetter stack rules.", reset.Body);
        Assert.False(again.Get("fyrd-stacks")!.UpdateAvailable);
    }

    [Fact]
    public void DeletedSkill_StaysDeleted_UntilReset()
    {
        var store = Make();
        Assert.True(store.Delete("fyrd-stacks"));
        Assert.False(store.Delete("fyrd-stacks"));
        var again = Make();
        Assert.Null(again.Get("fyrd-stacks"));
        Assert.Equal(["fyrd-events"], again.List().Select(s => s.Name));
        Assert.Equal("# Stacks\n\nStack rules.", again.Reset("fyrd-stacks").Body);
        Assert.NotNull(again.Get("fyrd-stacks"));
    }

    [Fact]
    public void OwnerSkill_IsSaved_HasNoSeed_AndCannotBeReset()
    {
        var store = Make();
        var saved = store.Save(new SessionSkill("fyrd-mine", "  Mine: use it.  ", ["builder", "builder", " reviewer "], "  Line one.\r\nLine two.  ", SeedHash: "ignored"));
        Assert.Equal("Mine: use it.", saved.Description);
        Assert.Equal(["builder", "reviewer"], saved.Roles);
        Assert.Equal("Line one.\nLine two.", saved.Body);
        Assert.Null(saved.SeedHash);
        Assert.Equal(saved.Body, Make().Get("fyrd-mine")!.Body);
        Assert.Throws<KeyNotFoundException>(() => store.Reset("fyrd-mine"));
    }

    [Fact]
    public void ASkillTheOwnerMadeFirst_IsNotOverwrittenByALaterSeedOfThatName()
    {
        var store = Make();
        store.Save(Skill("fyrd-later", body: "Owner's."));
        Seed("fyrd-later", "Later: a seed.", "builder", "Seed's.");
        var again = Make().Get("fyrd-later")!;
        Assert.Equal("Owner's.", again.Body);
        Assert.True(again.UpdateAvailable);
    }

    [Fact]
    public void SeedsThatAreNotValidSkills_AreSkipped()
    {
        Seed("fyrd-nameless", "Other name.", "builder", "Body.", nameInFile: "fyrd-other");
        Seed("fyrd-roleless", "No roles.", "", "Body.");
        Seed("not-fyrd", "Wrong prefix.", "builder", "Body.");
        Directory.CreateDirectory(Path.Combine(_seeds, "fyrd-empty"));
        Assert.Equal(["fyrd-events", "fyrd-stacks"], Make().List().Select(s => s.Name));
    }

    [Fact]
    public void AStoredFileThatIsNotAValidSkill_IsNotListed()
    {
        Make().List();
        File.WriteAllText(Path.Combine(_home.Paths.HubSessionSkillsDir, "fyrd-broken.json"), "{ not json");
        File.WriteAllText(Path.Combine(_home.Paths.HubSessionSkillsDir, "fyrd-bodyless.json"), "{\"name\":\"fyrd-bodyless\",\"description\":\"d\",\"roles\":[\"builder\"],\"body\":\"\"}");
        Assert.Equal(["fyrd-events", "fyrd-stacks"], Make().List().Select(s => s.Name));
    }

    [Theory]
    [InlineData("stacks")]
    [InlineData("fyrd-")]
    [InlineData("fyrd-Stacks")]
    [InlineData("fyrd-a--b")]
    [InlineData("fyrd-a-")]
    [InlineData("fyrd-a b")]
    [InlineData("fyrd-a/../b")]
    [InlineData("")]
    public void Save_RejectsANameThatIsNotFyrdAndHyphenatedLowerCase(string name)
    {
        Assert.Throws<ArgumentException>(() => Make().Save(Skill(name)));
        Assert.Null(Make().Get(name));
    }

    [Fact]
    public void Save_RejectsANameOverSixtyFourCharacters()
    {
        Make().Save(Skill("fyrd-" + new string('a', 59)));
        Assert.Throws<ArgumentException>(() => Make().Save(Skill("fyrd-" + new string('a', 60))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("two\nlines")]
    public void Save_RejectsAnEmptyOrMultiLineDescription(string description) =>
        Assert.Throws<ArgumentException>(() => Make().Save(Skill(description: description)));

    [Fact]
    public void Save_RejectsADescriptionOverTheLimit() =>
        Assert.Throws<ArgumentException>(() => Make().Save(Skill(description: new string('d', SessionSkills.MaxDescriptionLength + 1))));

    [Theory]
    [InlineData("")]
    [InlineData(" \n ")]
    [InlineData("bell \a")]
    public void Save_RejectsAnEmptyBodyOrOneWithControlCharacters(string body) =>
        Assert.Throws<ArgumentException>(() => Make().Save(Skill(body: body)));

    /// <summary>The budget is pinned: at most 1,500 words, comfortably under the 5,000 tokens a session keeps of a skill after compaction.</summary>
    [Fact]
    public void Save_HoldsABodyToTheWordBudget()
    {
        Assert.Equal(1500, SessionSkills.MaxWords);
        var store = Make();
        store.Save(Skill(body: string.Join(' ', Enumerable.Repeat("word", 1500))));
        var e = Assert.Throws<ArgumentException>(() => store.Save(Skill(body: string.Join('\n', Enumerable.Repeat("word", 1501)))));
        Assert.Contains("1501 words", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_RejectsNoRoles_AndRolesThatAreNotPeasantPersonaIds()
    {
        var store = Make();
        Assert.Throws<ArgumentException>(() => store.Save(new SessionSkill("fyrd-mine", "d", [], "b")));
        Assert.Throws<ArgumentException>(() => store.Save(new SessionSkill("fyrd-mine", "d", [" "], "b")));
        foreach (var role in new[] { "controller", "shared", "Builder", "a b" })
            Assert.Throws<ArgumentException>(() => store.Save(Skill(roles: role)));
    }

    [Fact]
    public void Hash_IsTheSameInAnyOrder_AndChangesWithEveryField()
    {
        var a = Skill("fyrd-a", "A.", "Body a.", "builder", "monitor");
        var b = Skill("fyrd-b", "B.", "Body b.");
        var hash = SessionSkills.Hash([a, b]);
        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.Equal(hash, SessionSkills.Hash([b, a]));
        Assert.Equal(hash, SessionSkills.Hash([b, a with { Roles = ["monitor", "builder"], SeedHash = "x", UpdateAvailable = true }]));
        Assert.NotEqual(hash, SessionSkills.Hash([a]));
        Assert.NotEqual(hash, SessionSkills.Hash([a with { Name = "fyrd-c" }, b]));
        Assert.NotEqual(hash, SessionSkills.Hash([a with { Description = "A!" }, b]));
        Assert.NotEqual(hash, SessionSkills.Hash([a with { Roles = ["builder"] }, b]));
        Assert.NotEqual(hash, SessionSkills.Hash([a with { Body = "Body a!" }, b]));
        // Text can't move between fields or skills without changing the hash.
        Assert.NotEqual(SessionSkills.Hash([Skill("fyrd-a", "A.", "x\ny")]), SessionSkills.Hash([Skill("fyrd-a", "A.\nx", "y")]));
        Assert.NotEqual(SessionSkills.Hash([]), SessionSkills.Hash([a]));
    }

    [Fact]
    public void Mode_IsInstalledOnlyWhileTheConfirmedHashIsTheHashOfTheSkills()
    {
        var skills = Make().List();
        Assert.Equal(SessionSkillMode.Inline, SessionSkills.Mode(new FyrdConfig(), skills));
        var confirmed = new FyrdConfig(SessionSkillsHash: SessionSkills.Hash(skills));
        Assert.Equal(SessionSkillMode.Installed, SessionSkills.Mode(confirmed, skills));
        var edited = skills.Select(s => s.Name == "fyrd-stacks" ? s with { Body = s.Body + " More." } : s).ToList();
        Assert.Equal(SessionSkillMode.OutOfDate, SessionSkills.Mode(confirmed, edited));
        Assert.Equal(SessionSkillMode.OutOfDate, SessionSkills.Mode(confirmed, [.. skills.Skip(1)]));
    }

    private static readonly HubPersona Builder = new("builder", "Builder", "claude-sonnet-5-5", "BUILDER-DEF", false);

    [Fact]
    public void Compose_Inline_AppendsEachBodyOfTheRole_AsSections_InNameOrder()
    {
        var skills = new[] { Skill("fyrd-b", "B.", "# Second\n\nTwo."), Skill("fyrd-a", "A.", "No heading."), Skill("fyrd-m", "M.", "# Monitor only", "monitor") };
        Assert.Equal("BUILDER-DEF\n\n# fyrd-a\n\nNo heading.\n\n# Second\n\nTwo.", SessionSkills.Compose(Builder, skills, installed: false));
    }

    [Fact]
    public void Compose_Installed_AppendsOneLinePerSkill_UnderTheRuleToLoadItOrAskTheOwner()
    {
        var skills = new[] { Skill("fyrd-b", "Second: use for two.", "# Second\n\nTwo."), Skill("fyrd-a", "First: use for one.", "# First\n\nOne.") };
        var text = SessionSkills.Compose(Builder, skills, installed: true);
        Assert.Equal(
            "BUILDER-DEF\n\n# Session skills\n\n"
            + "Some sections of your instructions are skills in your session, not text here. Before you act on what a line below covers, load its skill with the Skill tool. If the skill is not in your skill list, stop and ask the owner.\n\n"
            + "- `fyrd-a`: First: use for one.\n"
            + "- `fyrd-b`: Second: use for two.",
            text);
        Assert.DoesNotContain("Two.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_LeavesAPersonaWithoutSessionSkillsAlone()
    {
        var reviewer = Builder with { Id = "reviewer" };
        foreach (var installed in new[] { true, false })
        {
            Assert.Equal("BUILDER-DEF", SessionSkills.Compose(reviewer, [Skill()], installed));
            Assert.Equal("BUILDER-DEF", SessionSkills.Compose(Builder, [], installed));
            Assert.Equal("BUILDER-DEF", SessionSkills.Compose(Builder, null, installed));
        }
    }
}

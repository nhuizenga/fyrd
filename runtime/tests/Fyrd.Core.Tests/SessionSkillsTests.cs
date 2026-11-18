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
            Assert.Equal(SessionSkills.ContentHash(s), s.SeedHash);
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
        Assert.Equal(SessionSkills.ContentHash(after), after.SeedHash);
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
    public void MissingSeeds_AreTheDefaultsTheOwnerDeleted_AndResetBringsOneBack()
    {
        var store = Make();
        Assert.Empty(store.MissingSeeds());
        store.Save(Skill());
        store.Delete("fyrd-stacks");
        store.Delete("fyrd-events");
        Assert.Equal(["fyrd-events", "fyrd-stacks"], store.MissingSeeds());
        Assert.Equal(["fyrd-events", "fyrd-stacks"], Make().MissingSeeds());
        store.Reset("fyrd-stacks");
        Assert.Equal(["fyrd-events"], store.MissingSeeds());
    }

    /// <summary>The hash of one skill as it was stored as its <c>SeedHash</c> while that hash held the script template's version.</summary>
    private static string LegacySeedHash(SessionSkill s, int templateVersion)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new object[]
        {
            $"fyrd-session-skills-{templateVersion}",
            new object[] { s.Name, s.Description, s.Roles.Order(StringComparer.Ordinal).ToArray(), s.Body },
        });
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    private string StoreFile(string name) => Path.Combine(_home.Paths.HubSessionSkillsDir, name + ".json");

    /// <summary>Rewrites a stored skill the way an earlier version left it: with another seed hash and, when given, another body.</summary>
    private void Restore(string name, string seedHash, string? body = null)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(StoreFile(name)))!;
        Assert.NotNull(node["seedHash"]);
        node["seedHash"] = seedHash;
        if (body is not null) node["body"] = body;
        File.WriteAllText(StoreFile(name), node.ToJsonString());
    }

    private string StoredSeedHash(string name) =>
        System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(StoreFile(name)))!["seedHash"]!.GetValue<string>();

    /// <summary>
    /// A store written while a skill's seed hash held the script template's version (1, then 2): an unedited skill
    /// still follows a changed seed and goes with a removed one, an edited one is kept, and the seed hashes Fyrd can
    /// tell are rewritten as content hashes.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void AStoreWithSeedHashesInAnEarlierForm_IsReadAsBefore(int templateVersion)
    {
        Seed("fyrd-gone", "Gone: use it.", "builder", "# Gone\n\nGone rules.");
        Seed("fyrd-gone-edited", "Gone: use it.", "builder", "# Gone\n\nGone rules.");
        Seed("fyrd-edited", "Edited: use it.", "builder", "# Edited\n\nSeed text.");
        Seed("fyrd-edited-same-seed", "Edited: use it.", "builder", "# Edited\n\nSeed text.");
        Seed("fyrd-same", "Same: use it.", "builder", "# Same\n\nSame rules.");
        var seeded = Make().List();
        Assert.Equal(7, seeded.Count);
        foreach (var s in seeded)
        {
            var legacy = LegacySeedHash(s, templateVersion);
            Assert.NotEqual(s.SeedHash, legacy);
            Restore(s.Name, legacy, s.Name.Contains("edited", StringComparison.Ordinal) ? "Mine." : null);
        }
        var editedSameSeed = SessionSkills.ContentHash(seeded.Single(s => s.Name == "fyrd-edited-same-seed"));
        var editedOldSeed = LegacySeedHash(seeded.Single(s => s.Name == "fyrd-edited"), templateVersion);

        Seed("fyrd-stacks", "Stacks: use for a stack.", "builder", "# Stacks\n\nBetter stack rules.");
        Seed("fyrd-edited", "Edited: use it.", "builder", "# Edited\n\nBetter seed text.");
        Directory.Delete(Path.Combine(_seeds, "fyrd-gone"), true);
        Directory.Delete(Path.Combine(_seeds, "fyrd-gone-edited"), true);

        var store = Make();
        var list = store.List();
        Assert.Equal(["fyrd-edited", "fyrd-edited-same-seed", "fyrd-events", "fyrd-gone-edited", "fyrd-same", "fyrd-stacks"], list.Select(s => s.Name));
        SessionSkill Of(string name) => list.Single(s => s.Name == name);

        // Unedited: follows the changed seed, or only gets its seed hash in today's form.
        Assert.Equal("# Stacks\n\nBetter stack rules.", Of("fyrd-stacks").Body);
        foreach (var name in new[] { "fyrd-stacks", "fyrd-events", "fyrd-same" })
        {
            Assert.Equal(SessionSkills.ContentHash(Of(name)), Of(name).SeedHash);
            Assert.Equal(Of(name).SeedHash, StoredSeedHash(name));
            Assert.False(Of(name).UpdateAvailable);
        }
        Assert.Equal("# Same\n\nSame rules.", Of("fyrd-same").Body);

        // Edited: the text stays. Flagged only when the seed moved on since.
        Assert.Equal("Mine.", Of("fyrd-edited").Body);
        Assert.True(Of("fyrd-edited").UpdateAvailable);
        Assert.Equal(editedOldSeed, StoredSeedHash("fyrd-edited"));
        Assert.Equal("Mine.", Of("fyrd-edited-same-seed").Body);
        Assert.False(Of("fyrd-edited-same-seed").UpdateAvailable);
        Assert.Equal(editedSameSeed, StoredSeedHash("fyrd-edited-same-seed"));

        // The removed seeds: the unedited copy is gone, the edited one is the owner's now.
        Assert.False(File.Exists(StoreFile("fyrd-gone")));
        Assert.Equal("Mine.", Of("fyrd-gone-edited").Body);
        Assert.False(Of("fyrd-gone-edited").UpdateAvailable);
        Assert.Equal("fyrd-edited\nfyrd-edited-same-seed\nfyrd-events\nfyrd-gone-edited\nfyrd-same\nfyrd-stacks\n",
            File.ReadAllText(Path.Combine(_home.Paths.HubSessionSkillsDir, ".seeded")));

        // And it stays so on the next start.
        Assert.Equal(list, Make().List(), SkillComparer);
    }

    private static readonly IEqualityComparer<SessionSkill> SkillComparer = EqualityComparer<SessionSkill>.Create((a, b) =>
        a!.Name == b!.Name && a.Description == b.Description && a.Roles.SequenceEqual(b.Roles) && a.Body == b.Body
        && a.SeedHash == b.SeedHash && a.UpdateAvailable == b.UpdateAvailable);

    /// <summary>
    /// A skill's seed hash is its text and nothing else, pinned here: were the script's template version (or anything
    /// else that changes between releases) part of it, the next change would turn every unedited skill into an edited
    /// one that no longer follows its seed. The hash of the script does hold the version.
    /// </summary>
    [Fact]
    public void ASkillsSeedHash_IsItsTextOnly_NotTheScriptTemplateVersion()
    {
        var skill = Skill("fyrd-a", "A: use it.", "Body a.", "monitor", "builder");
        const string pinned = "82aa0cd1115609a689981e0101e4bfcf582f9fd4c0ed4f7646d27ccd76430b9f";
        Assert.Equal(pinned, SessionSkills.ContentHash(skill));
        Assert.Equal(pinned, SessionSkills.ContentHash(skill with { Roles = ["builder", "monitor"], SeedHash = "x", UpdateAvailable = true }));
        Assert.NotEqual(pinned, SessionSkills.ContentHash(skill with { Body = "Body a!" }));
        Assert.NotEqual(pinned, SessionSkills.ContentHash(skill with { Description = "A: use it!" }));
        Assert.NotEqual(pinned, SessionSkills.ContentHash(skill with { Roles = ["builder"] }));
        Assert.NotEqual(pinned, SessionSkills.ContentHash(skill with { Name = "fyrd-b" }));

        Assert.Equal(LegacySeedHash(skill, SessionSkillScript.TemplateVersion), SessionSkills.Hash([skill]));
        Assert.NotEqual(pinned, SessionSkills.Hash([skill]));
        for (var version = 0; version <= SessionSkillScript.TemplateVersion + 1; version++)
            Assert.NotEqual(pinned, LegacySeedHash(skill, version));

        // A store seeded today, read when the script's hash has moved on: its skills are unedited whatever that hash is.
        Make().List();
        Seed("fyrd-stacks", "Stacks: use for a stack.", "builder", "# Stacks\n\nBetter stack rules.");
        var after = Make().Get("fyrd-stacks")!;
        Assert.Equal("# Stacks\n\nBetter stack rules.", after.Body);
        Assert.NotEqual(SessionSkills.Hash([after]), after.SeedHash);
    }

    /// <summary>
    /// A seed removed from the repo: the unedited skill that came from it goes with it (an unedited skill follows its
    /// seed), an edited one stays as the owner's own, and the name can be seeded again later.
    /// </summary>
    [Fact]
    public void ARemovedSeed_TakesItsUneditedSkillWithIt_AndLeavesAnEditedOne()
    {
        var store = Make();
        store.Save(store.Get("fyrd-events")! with { Body = "Mine." });
        store.Save(Skill());
        Directory.Delete(Path.Combine(_seeds, "fyrd-stacks"), true);
        Directory.Delete(Path.Combine(_seeds, "fyrd-events"), true);

        var again = Make();
        Assert.Equal(["fyrd-events", "fyrd-mine"], again.List().Select(s => s.Name));
        var kept = again.Get("fyrd-events")!;
        Assert.Equal("Mine.", kept.Body);
        Assert.False(kept.UpdateAvailable);
        Assert.Empty(again.MissingSeeds());
        Assert.Throws<KeyNotFoundException>(() => again.Reset("fyrd-stacks"));
        Assert.Equal("fyrd-events\n", File.ReadAllText(Path.Combine(_home.Paths.HubSessionSkillsDir, ".seeded")));

        // The seed comes back: it is seeded as new.
        Seed("fyrd-stacks", "Stacks: use for a stack.", "builder", "# Stacks\n\nStack rules.");
        Assert.Equal("# Stacks\n\nStack rules.", Make().Get("fyrd-stacks")!.Body);
    }

    /// <summary>A seed that is there but no longer a valid skill removes nothing; a skill the owner deleted has nothing to remove.</summary>
    [Fact]
    public void ASeedThatBecameInvalid_OrWhoseSkillTheOwnerDeleted_RemovesNothing()
    {
        var store = Make();
        store.Delete("fyrd-events");
        Seed("fyrd-stacks", "Stacks: use for a stack.", "", "# Stacks\n\nStack rules.");
        Directory.Delete(Path.Combine(_seeds, "fyrd-events"), true);
        var again = Make();
        Assert.Equal(["fyrd-stacks"], again.List().Select(s => s.Name));
        Assert.Equal("# Stacks\n\nStack rules.", again.Get("fyrd-stacks")!.Body);
    }

    /// <summary>A quoted description (valid YAML when it holds ": ") is read without its quotes, as the Controller skills' are.</summary>
    [Fact]
    public void ParseSeed_ReadsAQuotedDescription_WithoutItsQuotes()
    {
        var quoted = SessionSkills.ParseSeed("fyrd-q", "---\nname: fyrd-q\ndescription: \"Stacks: say \\\"when\\\" and C:\\\\dir.\"\nroles: builder, monitor\n---\r\n# Q\r\n\r\nBody.\r\n")!;
        Assert.Equal("Stacks: say \"when\" and C:\\dir.", quoted.Description);
        Assert.Equal(["builder", "monitor"], quoted.Roles);
        Assert.Equal("# Q\n\nBody.", quoted.Body);
        // What the script writes for a session reads back as the same description.
        Assert.True(FrontMatter.TryParse(SessionSkillScript.SkillFile(quoted), out var fields, out var body));
        Assert.Equal(quoted.Description, fields["description"]);
        Assert.Equal(quoted.Body, body);
        var plain = SessionSkills.ParseSeed("fyrd-q", "---\ndescription: Stacks: plain \"inner\" quotes.\nroles: builder\n---\nBody.")!;
        Assert.Equal("Stacks: plain \"inner\" quotes.", plain.Description);
        Assert.Null(SessionSkills.ParseSeed("fyrd-q", "no front matter"));
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
    [InlineData("two\u2028lines")]
    [InlineData("two\u2029paragraphs")]
    [InlineData("next\u0085line")]
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
            + "Some sections of your instructions are skills in your session, not text here. Before you act on what a line below covers, load its skill with the Skill tool. "
            + $"Before the first load, check once that the file `/root/.claude/fyrd-session-skills/hash` holds exactly `{SessionSkills.Hash(skills)}`. "
            + "If the file is missing or holds anything else, or the skill is not in your skill list, stop and ask the owner.\n\n"
            + "- `fyrd-a`: First: use for one.\n"
            + "- `fyrd-b`: Second: use for two.",
            text);
        Assert.DoesNotContain("Two.", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The hash a session checks is the hash of every skill, not only its role's: it is what the setup script writes
    /// to its hash file, and what the owner confirmed.
    /// </summary>
    [Fact]
    public void Compose_Installed_NamesTheHashOfAllSkills_WhichIsTheOneTheScriptWrites()
    {
        var skills = new[] { Skill("fyrd-a", "First: use for one.", "One."), Skill("fyrd-m", "M.", "Monitor only.", "monitor") };
        var hash = SessionSkills.Hash(skills);
        var text = SessionSkills.Compose(Builder, skills, installed: true);
        Assert.Contains($"check once that the file `{SessionSkillScript.HashFile}` holds exactly `{hash}`.", text, StringComparison.Ordinal);
        Assert.Contains($"[ \"$fyrd_ok\" = 1 ] && printf '%s\\n' {hash} > \"$fyrd_state/hash\"", SessionSkillScript.Render(skills), StringComparison.Ordinal);
        Assert.DoesNotContain("fyrd-m", text, StringComparison.Ordinal);
        // Inline text names no hash: nothing in the environment is relied on.
        Assert.DoesNotContain(hash, SessionSkills.Compose(Builder, skills, installed: false), StringComparison.Ordinal);
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

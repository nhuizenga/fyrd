using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fyrd.Core.Config;
using Fyrd.Core.Hub;
using Microsoft.Extensions.DependencyInjection;

namespace Fyrd.Server.Tests;

public class SessionSkillApiTests
{
    private const string Stacks = "fyrd-stacked-pull-requests";

    private static async Task<JsonElement> ReadJson(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private static async Task<JsonElement> Get(HttpClient c, string path)
    {
        var r = await c.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await ReadJson(r);
    }

    private static Task<JsonElement> Script(HttpClient c) => Get(c, "/api/hub/session-skills/setup-script");

    private static async Task<string> BuilderPrompt(HttpClient c) =>
        (await Get(c, "/api/hub/personas/builder/prompt")).GetProperty("text").GetString()!;

    private static Task<HttpResponseMessage> Confirm(HttpClient c, string? hash) =>
        c.PostAsJsonAsync("/api/hub/session-skills/setup-script/confirm", new { hash });

    private static object Skill(string description = "Mine: use it.", string body = "# Mine\n\nMy rule.", params string[] roles) =>
        new { description, body, roles = roles.Length == 0 ? ["builder"] : roles };

    [Fact]
    public async Task List_ReturnsTheSeededSkills()
    {
        using var f = new FyrdFactory();
        var list = await Get(f.CreateClient(), "/api/hub/session-skills");
        Assert.Equal([Stacks], list.EnumerateArray().Select(s => s.GetProperty("name").GetString()));
        var stacks = list[0];
        Assert.Equal(["builder"], stacks.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        Assert.StartsWith("Stacked pull requests: ", stacks.GetProperty("description").GetString());
        Assert.StartsWith("# Stacked pull requests\n", stacks.GetProperty("body").GetString());
        Assert.False(stacks.GetProperty("updateAvailable").GetBoolean());
    }

    [Fact]
    public async Task PutGetResetDelete()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        var put = await c.PutAsJsonAsync("/api/hub/session-skills/fyrd-mine", Skill());
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("fyrd-mine", (await ReadJson(put)).GetProperty("name").GetString());
        Assert.Equal("# Mine\n\nMy rule.", (await Get(c, "/api/hub/session-skills/fyrd-mine")).GetProperty("body").GetString());
        // A skill the owner made has no default; a seeded one goes back to its seed.
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsync("/api/hub/session-skills/fyrd-mine/reset", null)).StatusCode);
        var seed = (await Get(c, $"/api/hub/session-skills/{Stacks}")).GetProperty("body").GetString();
        await c.PutAsJsonAsync($"/api/hub/session-skills/{Stacks}", Skill("Stacks: mine.", "Mine."));
        Assert.Equal("Mine.", (await Get(c, $"/api/hub/session-skills/{Stacks}")).GetProperty("body").GetString());
        var reset = await c.PostAsync($"/api/hub/session-skills/{Stacks}/reset", null);
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Equal(seed, (await ReadJson(reset)).GetProperty("body").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync("/api/hub/session-skills/fyrd-mine")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/hub/session-skills/fyrd-mine")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.DeleteAsync("/api/hub/session-skills/fyrd-mine")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.DeleteAsync("/api/hub/session-skills/Bad_Name")).StatusCode);
    }

    [Fact]
    public async Task Put_WithoutAName_TakesItFromTheBody()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        var put = await c.PutAsJsonAsync("/api/hub/session-skills", new { name = "fyrd-mine", description = "Mine: use it.", body = "Rule.", roles = new[] { "builder" } });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/hub/session-skills/fyrd-mine")).StatusCode);
    }

    [Fact]
    public async Task Put_ValidatesNameDescriptionBodyRolesAndTheWordBudget()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        async Task Refused(string name, object body, string detail)
        {
            var r = await c.PutAsJsonAsync($"/api/hub/session-skills/{name}", body);
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            var text = (await ReadJson(r)).GetProperty("detail").GetString();
            Assert.Contains(detail, text, StringComparison.Ordinal);
            // The owner reads this text: no parameter name of the code is in it.
            Assert.DoesNotContain("(Parameter", text, StringComparison.Ordinal);
        }
        await Refused("mine", Skill(), "'fyrd-' followed by");
        await Refused("fyrd-Mine", Skill(), "'fyrd-' followed by");
        await Refused("fyrd-mine", Skill(description: " "), "Description is required");
        await Refused("fyrd-mine", Skill(description: "two\nlines"), "one line");
        await Refused("fyrd-mine", Skill(description: "two\u2028lines"), "Description must be one line.");
        await Refused("fyrd-mine", Skill(body: " "), "Body is required");
        await Refused("fyrd-mine", Skill(body: string.Join(' ', Enumerable.Repeat("word", 1501))), "1501 words; a session skill holds at most 1500");
        await Refused("fyrd-mine", new { description = "d", body = "b", roles = Array.Empty<string>() }, "at least one role");
        await Refused("fyrd-mine", new { description = "d", body = "b" }, "at least one role");
        await Refused("fyrd-mine", Skill(roles: "controller"), "not a role");
        await Refused("fyrd-mine", Skill(roles: "nobody"), "There is no persona 'nobody'.");
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/hub/session-skills/fyrd-mine")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync("/api/hub/session-skills/fyrd-mine", Skill(body: string.Join(' ', Enumerable.Repeat("word", 1500))))).StatusCode);
    }

    [Fact]
    public async Task Write_WithForeignOrigin_Returns403()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        foreach (var (method, path) in new[]
        {
            (HttpMethod.Put, "/api/hub/session-skills/fyrd-mine"), (HttpMethod.Delete, $"/api/hub/session-skills/{Stacks}"),
            (HttpMethod.Post, "/api/hub/session-skills/setup-script/confirm"), (HttpMethod.Delete, "/api/hub/session-skills/setup-script/confirm"),
        })
        {
            var req = new HttpRequestMessage(method, path) { Content = JsonContent.Create(Skill()) };
            req.Headers.Add("Origin", "http://evil.example");
            Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(req)).StatusCode);
        }
        Assert.Equal(1, (await Get(c, "/api/hub/session-skills")).GetArrayLength());
    }

    [Fact]
    public async Task SetupScript_IsTheBlockForTheStoredSkills_WithTheModeAndTheEnvironment()
    {
        using var f = new FyrdFactory();
        var config = f.Services.GetRequiredService<ConfigStore>();
        config.Save(config.Load() with { EnvironmentId = "env_test1" });
        var c = f.CreateClient();
        var state = await Script(c);
        var skills = f.Services.GetRequiredService<SessionSkills>().List();
        var hash = SessionSkills.Hash(skills);
        Assert.Equal("inline", state.GetProperty("mode").GetString());
        Assert.Equal(hash, state.GetProperty("hash").GetString());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("confirmedHash").ValueKind);
        Assert.Equal("env_test1", state.GetProperty("environmentId").GetString());
        Assert.Equal("/root/.claude/skills", state.GetProperty("skillsDir").GetString());
        Assert.Equal("/root/.claude/fyrd-session-skills/hash", state.GetProperty("hashFile").GetString());
        Assert.Equal([Stacks], state.GetProperty("skills").EnumerateArray().Select(s => s.GetString()));
        var script = state.GetProperty("script").GetString()!;
        Assert.Equal(SessionSkillScript.Render(skills), script);
        Assert.StartsWith($"# >>> fyrd session skills {hash} >>>\n", script, StringComparison.Ordinal);
        Assert.EndsWith("# <<< fyrd session skills <<<\n", script, StringComparison.Ordinal);
        Assert.Contains("POST /repos/{owner}/{repo}/stacks", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Confirm_RecordsTheCurrentHash_AndTheBuilderGetsPointers_UntilASkillIsEdited()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        var inline = await BuilderPrompt(c);
        Assert.Contains("# Stacked pull requests\n", inline, StringComparison.Ordinal);
        Assert.Contains("POST /repos/{owner}/{repo}/stacks", inline, StringComparison.Ordinal);
        Assert.DoesNotContain("# Session skills", inline, StringComparison.Ordinal);

        var hash = (await Script(c)).GetProperty("hash").GetString()!;
        var confirmed = await Confirm(c, hash);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        var state = await ReadJson(confirmed);
        Assert.Equal("installed", state.GetProperty("mode").GetString());
        Assert.Equal(hash, state.GetProperty("confirmedHash").GetString());
        Assert.Equal(hash, f.Services.GetRequiredService<ConfigStore>().Load().SessionSkillsHash);
        var prompt = await Get(c, "/api/hub/personas/builder/prompt");
        Assert.Equal("installed", prompt.GetProperty("mode").GetString());
        var installed = prompt.GetProperty("text").GetString()!;
        Assert.Contains($"\n- `{Stacks}`: Stacked pull requests: ", installed, StringComparison.Ordinal);
        // The session is told which skills its environment must hold: the hash the owner confirmed.
        Assert.Contains($"check once that the file `/root/.claude/fyrd-session-skills/hash` holds one line, exactly `{hash}`. If the file is missing or holds anything else, or the skill is not in your skill list, stop and ask the owner.", installed, StringComparison.Ordinal);
        Assert.DoesNotContain("POST /repos/{owner}/{repo}/stacks", installed, StringComparison.Ordinal);
        Assert.True(prompt.GetProperty("words").GetInt32() < SessionSkills.WordCount(inline));
        // A persona without session skills is the stored text in either mode.
        Assert.Equal((await Get(c, "/api/hub/personas/reviewer")).GetProperty("definition").GetString(),
            (await Get(c, "/api/hub/personas/reviewer/prompt")).GetProperty("text").GetString());

        // Any edit makes the pasted script stale: the Builder gets the whole text again, with the edit.
        var skill = await Get(c, $"/api/hub/session-skills/{Stacks}");
        var edited = skill.GetProperty("body").GetString() + "\n- An added rule.";
        await c.PutAsJsonAsync($"/api/hub/session-skills/{Stacks}", Skill(skill.GetProperty("description").GetString()!, edited));
        state = await Script(c);
        Assert.Equal("out-of-date", state.GetProperty("mode").GetString());
        Assert.Equal(hash, state.GetProperty("confirmedHash").GetString());
        Assert.NotEqual(hash, state.GetProperty("hash").GetString());
        var after = await Get(c, "/api/hub/personas/builder/prompt");
        Assert.Equal("out-of-date", after.GetProperty("mode").GetString());
        Assert.Contains("POST /repos/{owner}/{repo}/stacks", after.GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.EndsWith("\n- An added rule.", after.GetProperty("text").GetString(), StringComparison.Ordinal);

        // The old script can't be confirmed again; the new one can.
        Assert.Equal(HttpStatusCode.Conflict, (await Confirm(c, hash)).StatusCode);
        Assert.Equal("out-of-date", (await Script(c)).GetProperty("mode").GetString());
        Assert.Equal(HttpStatusCode.OK, (await Confirm(c, state.GetProperty("hash").GetString())).StatusCode);
        Assert.Equal("installed", (await Script(c)).GetProperty("mode").GetString());
    }

    [Fact]
    public async Task AddingOrDeletingASkill_AlsoMakesTheConfirmedScriptStale()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        await Confirm(c, (await Script(c)).GetProperty("hash").GetString());
        await c.PutAsJsonAsync("/api/hub/session-skills/fyrd-mine", Skill());
        Assert.Equal("out-of-date", (await Script(c)).GetProperty("mode").GetString());
        Assert.Contains("\n\n# Mine\n\nMy rule.\n\n# Stacked pull requests\n", await BuilderPrompt(c), StringComparison.Ordinal);
        await c.DeleteAsync("/api/hub/session-skills/fyrd-mine");
        Assert.Equal("installed", (await Script(c)).GetProperty("mode").GetString());
        await c.DeleteAsync($"/api/hub/session-skills/{Stacks}");
        Assert.Equal("out-of-date", (await Script(c)).GetProperty("mode").GetString());
        Assert.DoesNotContain(Stacks, (await Script(c)).GetProperty("script").GetString()!.Split('\n').Where(l => l.Contains("mkdir", StringComparison.Ordinal)).Aggregate("", string.Concat), StringComparison.Ordinal);
    }

    /// <summary>A default the owner deleted is named by the setup endpoint, and reset brings it back.</summary>
    [Fact]
    public async Task ADeletedDefault_IsListedAsMissing_UntilItIsReset()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        await c.PutAsJsonAsync("/api/hub/session-skills/fyrd-mine", Skill());
        Assert.Empty((await Script(c)).GetProperty("missingDefaults").EnumerateArray());
        await c.DeleteAsync("/api/hub/session-skills/fyrd-mine");
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/hub/session-skills/{Stacks}")).StatusCode);
        Assert.Equal([Stacks], (await Script(c)).GetProperty("missingDefaults").EnumerateArray().Select(n => n.GetString()));
        Assert.DoesNotContain("POST /repos/{owner}/{repo}/stacks", await BuilderPrompt(c), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync($"/api/hub/session-skills/{Stacks}/reset", null)).StatusCode);
        Assert.Empty((await Script(c)).GetProperty("missingDefaults").EnumerateArray());
        Assert.Contains("POST /repos/{owner}/{repo}/stacks", await BuilderPrompt(c), StringComparison.Ordinal);
    }

    /// <summary>
    /// The Controller message as the running server builds it (the ControllerManager from its own services, not only the
    /// prompt endpoint): the Builder's block carries the session skills, whole or as lines with the confirmed hash.
    /// </summary>
    [Fact]
    public async Task TheServersControllerMessage_CarriesTheSessionSkills_InTheModeTheConfirmationGives()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        var controller = f.Services.GetRequiredService<ControllerManager>();
        string BuilderBlock()
        {
            var message = controller.BuildPrompt().Prompt;
            var start = message.IndexOf("<peasant-persona id=\"builder\"", StringComparison.Ordinal);
            return message[start..message.IndexOf("</peasant-persona>", start, StringComparison.Ordinal)];
        }

        var inline = BuilderBlock();
        Assert.EndsWith(">" + await BuilderPrompt(c), inline, StringComparison.Ordinal);
        Assert.Contains("# Stacked pull requests\n", inline, StringComparison.Ordinal);
        Assert.Contains("POST /repos/{owner}/{repo}/stacks", inline, StringComparison.Ordinal);
        var inlineHash = controller.BuildPrompt().InstructionsHash;

        var hash = (await Script(c)).GetProperty("hash").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await Confirm(c, hash)).StatusCode);
        var installed = BuilderBlock();
        Assert.EndsWith(">" + await BuilderPrompt(c), installed, StringComparison.Ordinal);
        Assert.Contains($"\n- `{Stacks}`: Stacked pull requests: ", installed, StringComparison.Ordinal);
        Assert.Contains($"holds one line, exactly `{hash}`", installed, StringComparison.Ordinal);
        Assert.DoesNotContain("POST /repos/{owner}/{repo}/stacks", installed, StringComparison.Ordinal);
        Assert.NotEqual(inlineHash, controller.BuildPrompt().InstructionsHash);

        await c.PutAsJsonAsync("/api/hub/session-skills/fyrd-mine", Skill());
        Assert.Contains("\n\n# Mine\n\nMy rule.\n\n# Stacked pull requests\n", BuilderBlock(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-hash")]
    public async Task Confirm_WithoutAHash_400(string? hash)
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await Confirm(c, hash)).StatusCode);
        Assert.Equal("inline", (await Script(c)).GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Clear_GoesBackToInline()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        await Confirm(c, (await Script(c)).GetProperty("hash").GetString());
        var cleared = await c.DeleteAsync("/api/hub/session-skills/setup-script/confirm");
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        var state = await ReadJson(cleared);
        Assert.Equal("inline", state.GetProperty("mode").GetString());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("confirmedHash").ValueKind);
        Assert.Contains("POST /repos/{owner}/{repo}/stacks", await BuilderPrompt(c), StringComparison.Ordinal);
    }

    /// <summary>
    /// The console saves settings by sending the whole config back. That keeps the confirmation, never sets one, and
    /// drops it when the environment changes: the script was pasted into the old one.
    /// </summary>
    [Fact]
    public async Task PutConfig_KeepsTheConfirmation_CannotSetIt_AndDropsItWhenTheEnvironmentChanges()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        await c.PutAsJsonAsync("/api/config", new { environmentId = "env_one", sessionCap = 20, sessionSkillsHash = new string('a', 64) });
        Assert.Equal("inline", (await Script(c)).GetProperty("mode").GetString());

        var hash = (await Script(c)).GetProperty("hash").GetString();
        await Confirm(c, hash);
        await c.PutAsJsonAsync("/api/config", new { environmentId = "env_one", sessionCap = 7 });
        Assert.Equal("installed", (await Script(c)).GetProperty("mode").GetString());
        await c.PutAsJsonAsync("/api/config", new { environmentId = "env_one", sessionCap = 7, sessionSkillsHash = (string?)null });
        Assert.Equal("installed", (await Script(c)).GetProperty("mode").GetString());

        await c.PutAsJsonAsync("/api/config", new { environmentId = "env_two", sessionCap = 7, sessionSkillsHash = hash });
        var state = await Script(c);
        Assert.Equal("inline", state.GetProperty("mode").GetString());
        Assert.Equal("env_two", state.GetProperty("environmentId").GetString());
    }

    [Fact]
    public async Task Prompt_OfAnUnknownPersona_404()
    {
        using var f = new FyrdFactory();
        Assert.Equal(HttpStatusCode.NotFound, (await f.CreateClient().GetAsync("/api/hub/personas/nope/prompt")).StatusCode);
    }

    [Fact]
    public async Task Guide_IsTheOnboardingDocument_And404WhenItIsMissing()
    {
        using var f = new FyrdFactory();
        var text = (await Get(f.CreateClient(), "/api/hub/session-skills/guide")).GetProperty("text").GetString()!;
        Assert.StartsWith("# Session skills", text, StringComparison.Ordinal);
        using var none = new FyrdFactory { DocsDir = Path.Combine(Path.GetTempPath(), "fyrd-no-docs-" + Guid.NewGuid()) };
        Assert.Equal(HttpStatusCode.NotFound, (await none.CreateClient().GetAsync("/api/hub/session-skills/guide")).StatusCode);
    }
}

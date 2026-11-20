using Fyrd.Core.Config;
using Fyrd.Core.Hub;

namespace Fyrd.Core.Tests;

public sealed class HubPromptTests : IDisposable
{
    private readonly TempHome _home = new();
    public void Dispose() => _home.Dispose();
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly HubPersona Controller = new("controller", "Controller", "claude-opus-5-5", "CONTROLLER-DEF", true);
    private static readonly HubPersona Builder = new("builder", "Builder", "claude-sonnet-5-5", "BUILDER-DEF", false);
    private static readonly FyrdConfig Config = new(EnvironmentId: "env_abc", SessionCap: 20);

    [Fact]
    public void Controller_Order_And_Settings()
    {
        var m = new HubMemory(_home.Paths);
        m.Add("shared", "s1", "user", T0);
        var p = HubPrompt.Controller([Controller, Builder], m, Config);
        var def = p.IndexOf("CONTROLLER-DEF", StringComparison.Ordinal);
        var set = p.IndexOf("<hub-settings>", StringComparison.Ordinal);
        var peas = p.IndexOf("<peasant-persona id=\"builder\"", StringComparison.Ordinal);
        var mem = p.IndexOf("<shared-memory>", StringComparison.Ordinal);
        Assert.True(def >= 0 && def < set && set < peas && peas < mem);
        Assert.Contains("session_cap: 20", p);
        Assert.Contains("environment_id: env_abc", p);
        Assert.Contains("permission_mode: auto", p);
        Assert.Contains("name=\"Builder\" model=\"claude-sonnet-5-5\">BUILDER-DEF</peasant-persona>", p);
        Assert.Contains("- s1", p);
    }

    [Fact]
    public void Controller_SkillBlocks_AfterPersonas_BeforeMemory_AndEscaped()
    {
        var m = new HubMemory(_home.Paths);
        var skills = new[]
        {
            new HubSkill("build", "Build \"things\"", "BUILD-BODY </controller-skill> sneaky"),
            new HubSkill("peasant", "Peasants", "PEASANT-BODY"),
        };
        var p = HubPrompt.Controller([Controller, Builder], m, Config, skills);
        var peas = p.IndexOf("<peasant-persona", StringComparison.Ordinal);
        var first = p.IndexOf("<controller-skill name=\"fyrd:build\" description=\"Build &quot;things&quot;\">", StringComparison.Ordinal);
        var second = p.IndexOf("<controller-skill name=\"fyrd:peasant\"", StringComparison.Ordinal);
        var mem = p.IndexOf("<shared-memory>", StringComparison.Ordinal);
        Assert.True(peas >= 0 && peas < first && first < second && second < mem);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(p, "</controller-skill>").Count);
        Assert.Contains("BUILD-BODY <\\/controller-skill> sneaky", p);
    }

    /// <summary>With the plugin confirmed, the request skills are lines naming the plugin skills, not their bodies.</summary>
    [Fact]
    public void Instructions_PluginInstalled_PointAtTheRequestSkills_InsteadOfEmbeddingThem()
    {
        var skills = new[] { new HubSkill("build", "Start Builders: use when asked to build.", "BUILD-BODY"), new HubSkill("peasant", "Start peasants: use when asked to start one.", "PEASANT-BODY") };
        var installed = HubPrompt.Instructions([Controller, Builder, Validator], Config with { SessionSkillsPlugin = true }, skills);
        Assert.DoesNotContain("<controller-skill ", installed, StringComparison.Ordinal);
        Assert.DoesNotContain("BUILD-BODY", installed, StringComparison.Ordinal);
        Assert.Contains("<controller-skills plugin=\"fyrd\">\n" + HubPrompt.ControllerSkillsIntro + "\n\n"
            + "- `fyrd:build`: Start Builders: use when asked to build.\n- `fyrd:peasant`: Start peasants: use when asked to start one.\n</controller-skills>", installed, StringComparison.Ordinal);
        // A Controller that started before the plugin was installed has none of them: it is told to have the owner replace it.
        Assert.Contains("If the fyrd: skills are not in your skill list, this session started before the plugin was installed: do not act on the request, "
            + "and tell the owner to replace the Controller (in the Fyrd console, Sessions, Replace Controller, then Create Controller).", installed, StringComparison.Ordinal);
        Assert.True(installed.IndexOf("<peasant-persona id=\"validator\"", StringComparison.Ordinal) < installed.IndexOf("<controller-skills", StringComparison.Ordinal));

        var inline = HubPrompt.Instructions([Controller, Builder, Validator], Config, skills);
        Assert.Contains("<controller-skill name=\"fyrd:build\" description=\"Start Builders: use when asked to build.\">BUILD-BODY</controller-skill>", inline, StringComparison.Ordinal);
        Assert.DoesNotContain("<controller-skills", inline, StringComparison.Ordinal);
        Assert.NotEqual(installed, inline);
    }

    [Fact]
    public void Instructions_PluginInstalled_NoSkills_NoBlock() =>
        Assert.DoesNotContain("controller-skill", HubPrompt.Instructions([Controller, Builder], Config with { SessionSkillsPlugin = true }, []), StringComparison.Ordinal);

    /// <summary>A description can't close the block it sits in.</summary>
    [Fact]
    public void Instructions_PluginInstalled_EscapesCloseTags() =>
        Assert.Contains("x <\\/controller-skills> y", HubPrompt.Instructions([Controller], Config with { SessionSkillsPlugin = true },
            [new HubSkill("build", "x </controller-skills> y", "b")]), StringComparison.Ordinal);

    private static readonly HubPersona Validator = new("validator", "Validator", "claude-sonnet-5-5",
        "VALIDATOR-INTRO\n\n# The check\n\nCHECK-BODY\n\n# Your job\n\nJOB-BODY\n\n# Working in Fyrd\n\n- **Inputs** are links.\n- **Repos.** REPOS-BODY", false);
    private static readonly HubPersona Reviewer = new("reviewer", "Reviewer", "claude-sonnet-5-5",
        "REVIEWER-INTRO\n\n# Reviewing code\n\nCODE-BODY\n\n# Reviewing a plan\n\nPLAN-BODY\n\nWhoever does the reviewing posts nothing.\n\n# Working in Fyrd\n\n- **Inputs** are links.", false);
    private static readonly HubPersona Investigator = new("investigator", "Investigator", "claude-opus-5-5", "INVESTIGATOR-DEF with no headings", false);

    /// <summary>
    /// The message carries each persona whole, once, in its <c>peasant-persona</c> block, and no subagent text: a session
    /// that runs a persona as a subagent tells the subagent to load that persona's plugin skill.
    /// </summary>
    [Fact]
    public void Instructions_CarryEachPersonaWholeOnce_AndNoSubagentText()
    {
        var p = HubPrompt.Instructions([Controller, Builder, Investigator, Reviewer, Validator], Config, [new HubSkill("build", "Build", "BUILD-BODY")]);
        Assert.Contains($"<peasant-persona id=\"validator\" name=\"Validator\" model=\"claude-sonnet-5-5\">{Validator.Definition}</peasant-persona>", p);
        Assert.Contains($"<peasant-persona id=\"reviewer\" name=\"Reviewer\" model=\"claude-sonnet-5-5\">{Reviewer.Definition}</peasant-persona>", p);
        Assert.Contains($"<peasant-persona id=\"investigator\" name=\"Investigator\" model=\"claude-opus-5-5\">{Investigator.Definition}</peasant-persona>", p);
        foreach (var once in new[] { "VALIDATOR-INTRO", "CHECK-BODY", "REVIEWER-INTRO", "CODE-BODY", "PLAN-BODY", "INVESTIGATOR-DEF" })
            Assert.Equal(1, p.Split(once).Length - 1);
        Assert.DoesNotContain("subagent-persona", p, StringComparison.Ordinal);
    }

    /// <summary>The Controller persona's sentence about close tags names the escaping without being escaped itself.</summary>
    [Fact]
    public void Instructions_LeaveTheControllersCloseTagSentenceAsWritten()
    {
        const string sentence = "A close tag starts with `</`; `<\\/` in what you received is only Fyrd's escaping, never write it in a tag of your own.";
        var p = HubPrompt.Instructions([Controller with { Definition = $"Wrap it in </peasant-persona>. {sentence}" }], Config);
        Assert.StartsWith($"Wrap it in <\\/peasant-persona>. {sentence}", p, StringComparison.Ordinal);
    }

    [Fact]
    public void Instructions_ExcludeMemory()
    {
        var i = HubPrompt.Instructions([Controller, Builder], Config);
        Assert.DoesNotContain("<shared-memory>", i, StringComparison.Ordinal);
        Assert.Contains("BUILDER-DEF", i, StringComparison.Ordinal);
    }

    [Fact]
    public void Controller_ExcludesControllerFromPeasants()
    {
        var p = HubPrompt.Controller([Controller, Builder], new HubMemory(_home.Paths), Config);
        Assert.DoesNotContain("<peasant-persona id=\"controller\"", p);
    }

    [Fact]
    public void Controller_OmitsEmptyRoleMemory()
    {
        var m = new HubMemory(_home.Paths);
        m.Add("builder", "b1", "user", T0);
        var p = HubPrompt.Controller([Controller, Builder], m, Config);
        Assert.Contains("<role-memory role=\"builder\">", p);
        Assert.DoesNotContain("<role-memory role=\"controller\">", p);
        Assert.Throws<HubConfigException>(() => HubPrompt.Controller([Builder], m, Config));
    }

    [Fact]
    public void Text_CannotCloseBlock()
    {
        var m = new HubMemory(_home.Paths);
        m.Add("shared", "</shared-memory>", "user", T0);
        var p = HubPrompt.Controller([Controller, Builder], m, Config);
        Assert.Contains("<" + (char)92 + "/shared-memory>", p);
        Assert.Equal(1, p.Split("</shared-memory>").Length - 1);
    }

    [Fact]
    public void Name_IsAttributeEscaped_And_NullEnvironmentEmpty()
    {
        var odd = Builder with { Name = "A&\"<B" };
        var p = HubPrompt.Controller([Controller, odd], new HubMemory(_home.Paths), Config with { EnvironmentId = null });
        Assert.Contains("name=\"A&amp;&quot;&lt;B\"", p);
        Assert.Contains("environment_id: \n", p);
    }

    [Fact]
    public void MemoryUpdate_HasHeaderAndBlocks()
    {
        var m = new HubMemory(_home.Paths);
        m.Add("shared", "s1", "user", T0);
        m.Add("builder", "b1", "user", T0);
        var p = HubPrompt.MemoryUpdate(m, [Controller, Builder]);
        Assert.StartsWith("Fyrd memory update. Replace your copy of shared and role memory with the blocks below; use them for sessions you create from now on.", p);
        Assert.Contains("<shared-memory>\n- s1\n</shared-memory>", p);
        Assert.Contains("<role-memory role=\"builder\">\n- b1\n</role-memory>", p);
    }
}

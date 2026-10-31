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

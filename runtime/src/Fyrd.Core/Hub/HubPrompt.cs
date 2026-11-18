using System.Text;
using System.Text.RegularExpressions;
using Fyrd.Core.Config;

namespace Fyrd.Core.Hub;

/// <summary>Static prompt builders for the hub Controller session.</summary>
public static partial class HubPrompt
{
    private const string ControllerId = "controller";

    [GeneratedRegex("</(?=(?:hub-settings|peasant-persona|subagent-persona|controller-skill|shared-memory|role-memory))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ClosingTagRegex();

    /// <summary>The full Controller message: <see cref="Instructions"/> followed by the memory blocks.</summary>
    public static string Controller(IReadOnlyList<HubPersona> personas, HubMemory memory, FyrdConfig config, IReadOnlyList<HubSkill>? skills = null) =>
        string.Join("\n\n", new[] { Instructions(personas, config, skills) }.Concat(MemoryBlocks(memory, personas)));

    /// <summary>
    /// Everything but memory: the Controller definition, settings, peasant personas, subagent persona texts and skills.
    /// A <c>subagent-persona-text</c> block holds what <see cref="SubagentPersonas"/> cuts from a persona; the Controller
    /// copies it, not the whole persona, into the <c>subagent-persona</c> blocks of the sessions that run that persona as a subagent.
    /// </summary>
    public static string Instructions(IReadOnlyList<HubPersona> personas, FyrdConfig config, IReadOnlyList<HubSkill>? skills = null)
    {
        var controller = personas.FirstOrDefault(p => p.Id == ControllerId)
            ?? throw new HubConfigException("The Controller persona is missing.");
        var sections = new List<string>
        {
            Safe(controller.Definition),
            $"<hub-settings>\nenvironment_id: {config.EnvironmentId ?? ""}\npermission_mode: auto\nsession_cap: {config.SessionCap}\n</hub-settings>",
        };
        foreach (var p in personas.Where(p => p.Id != ControllerId))
            sections.Add($"<peasant-persona id=\"{AttrEscape(p.Id)}\" name=\"{AttrEscape(p.Name)}\" model=\"{AttrEscape(p.Model)}\">{Safe(p.Definition)}</peasant-persona>");
        foreach (var t in SubagentPersonas.All(personas))
        {
            var target = t.For is null ? "" : $" for=\"{AttrEscape(t.For)}\"";
            sections.Add($"<subagent-persona-text id=\"{AttrEscape(t.Id)}\"{target}>{Safe(t.Text)}</subagent-persona-text>");
        }
        foreach (var s in skills ?? [])
            sections.Add($"<controller-skill name=\"{AttrEscape(s.Name)}\" description=\"{AttrEscape(s.Description)}\">{Safe(s.Body)}</controller-skill>");
        return string.Join("\n\n", sections);
    }

    public static string MemoryUpdate(HubMemory memory, IReadOnlyList<HubPersona> personas) =>
        string.Join("\n\n", new[]
        {
            "Fyrd memory update. Replace your copy of shared and role memory with the blocks below; use them for sessions you create from now on.",
        }.Concat(MemoryBlocks(memory, personas)));

    private static IEnumerable<string> MemoryBlocks(HubMemory memory, IReadOnlyList<HubPersona> personas)
    {
        yield return $"<shared-memory>\n{Safe(memory.Render(HubMemory.Shared))}\n</shared-memory>";
        foreach (var p in personas)
        {
            var text = memory.Render(p.Id);
            if (text.Length == 0) continue;
            yield return $"<role-memory role=\"{AttrEscape(p.Id)}\">\n{Safe(text)}\n</role-memory>";
        }
    }

    private static string Safe(string s) => ClosingTagRegex().Replace(s ?? "", "<\u005c/");

    private static string AttrEscape(string s) =>
        (s ?? "").Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;");
}

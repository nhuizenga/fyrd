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
    public static string Controller(IReadOnlyList<HubPersona> personas, HubMemory memory, FyrdConfig config, IReadOnlyList<HubSkill>? skills = null,
        IReadOnlyList<SessionSkill>? sessionSkills = null) =>
        string.Join("\n\n", new[] { Instructions(personas, config, skills, sessionSkills) }.Concat(MemoryBlocks(memory, personas)));

    /// <summary>
    /// Everything but memory: the Controller definition, settings, peasant personas, subagent persona texts and skills.
    /// A <c>subagent-persona-text</c> block holds what <see cref="SubagentPersonas"/> cuts from a persona; the Controller
    /// copies it, not the whole persona, into the <c>subagent-persona</c> blocks of the sessions that run that persona as a subagent.
    /// <para>
    /// A <c>peasant-persona</c> block holds what <see cref="SessionSkills.Compose"/> makes of the persona and its role's
    /// session skills, so whatever the Controller copies from the block into a session's system prompt carries them: a
    /// line per skill the fyrd plugin ships when the owner confirmed the environment installs it, else (and for a skill
    /// edited or added in the console) the skill's whole body. The subagent texts are cut from the stored personas and carry no session skills.
    /// </para>
    /// <para>
    /// The Controller request skills (<see cref="HubSkills"/>) are, the same way, one <c>controller-skills</c> block of
    /// lines naming the plugin skill <c>fyrd:&lt;name&gt;</c> when the owner confirmed the plugin, else one
    /// <c>controller-skill</c> block per skill, named <c>fyrd:&lt;name&gt;</c> too, with its whole body, so a Controller works without the plugin.
    /// </para>
    /// </summary>
    public static string Instructions(IReadOnlyList<HubPersona> personas, FyrdConfig config, IReadOnlyList<HubSkill>? skills = null,
        IReadOnlyList<SessionSkill>? sessionSkills = null)
    {
        var installed = SessionSkills.Mode(config) == SessionSkillMode.Installed;
        var controller = personas.FirstOrDefault(p => p.Id == ControllerId)
            ?? throw new HubConfigException("The Controller persona is missing.");
        var sections = new List<string>
        {
            Safe(controller.Definition),
            $"<hub-settings>\nenvironment_id: {config.EnvironmentId ?? ""}\npermission_mode: auto\nsession_cap: {config.SessionCap}\n</hub-settings>",
        };
        foreach (var p in personas.Where(p => p.Id != ControllerId))
            sections.Add($"<peasant-persona id=\"{AttrEscape(p.Id)}\" name=\"{AttrEscape(p.Name)}\" model=\"{AttrEscape(p.Model)}\">{Safe(SessionSkills.Compose(p, sessionSkills, installed))}</peasant-persona>");
        foreach (var t in SubagentPersonas.All(personas))
        {
            var target = t.For is null ? "" : $" for=\"{AttrEscape(t.For)}\"";
            sections.Add($"<subagent-persona-text id=\"{AttrEscape(t.Id)}\"{target}>{Safe(t.Text)}</subagent-persona-text>");
        }
        if (installed && skills is { Count: > 0 })
            sections.Add(ControllerSkillPointers(skills));
        else
            foreach (var s in skills ?? [])
                sections.Add($"<controller-skill name=\"{AttrEscape(SessionSkillScript.PluginSkillName(s.Name))}\" description=\"{AttrEscape(s.Description)}\">{Safe(s.Body)}</controller-skill>");
        return string.Join("\n\n", sections);
    }

    /// <summary>Said once above the request skills' lines when the Controller loads them from the fyrd plugin.</summary>
    public const string ControllerSkillsIntro =
        "Your request skills are skills of the fyrd plugin in your session, not text here. Before you act on a request a line below covers, load its skill with the Skill tool. "
        + "If the skill is not in your skill list, stop and tell the owner.";

    /// <summary>
    /// The request skills as one block of lines, a line per skill with the name the Controller loads it by
    /// (<c>fyrd:&lt;name&gt;</c>) and its description, sent instead of the bodies when the owner confirmed that the
    /// environment's setup script installs the fyrd plugin.
    /// </summary>
    private static string ControllerSkillPointers(IReadOnlyList<HubSkill> skills) =>
        "<controller-skills plugin=\"fyrd\">\n" + Safe(ControllerSkillsIntro + "\n\n"
            + string.Join("\n", skills.Select(s => $"- `{SessionSkillScript.PluginSkillName(s.Name)}`: {s.Description}"))) + "\n</controller-skills>";

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

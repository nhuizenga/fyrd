using System.Text;

namespace Fyrd.Core.Hub;

public sealed record HubSkill(string Name, string Description, string Body);

/// <summary>
/// The Controller request skills: the plugin skills <c>skills/&lt;name&gt;/SKILL.md</c> whose front matter says
/// <c>kind: controller-skill</c>, which a Controller with the fyrd plugin loads as <c>fyrd:&lt;name&gt;</c>. They are read
/// from disk on every call (the files are small), so a <c>git pull</c> reaches the Controller without a restart.
/// </summary>
public sealed class HubSkills(string dir)
{
    /// <summary>The front matter <c>kind</c> of a Controller request skill.</summary>
    public const string Kind = "controller-skill";

    /// <summary>
    /// Skills ordered by name; empty when the directory is missing. Front matter is simple single-line <c>key: value</c> pairs
    /// (see <see cref="FrontMatter"/>); a skill of another kind (a persona, a session skill) is skipped. Throws
    /// <see cref="InvalidDataException"/> for a SKILL.md without front matter, and IO errors propagate.
    /// </summary>
    public IReadOnlyList<HubSkill> List()
    {
        if (!Directory.Exists(dir)) return [];
        var skills = new List<HubSkill>();
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            var file = Path.Combine(sub, "SKILL.md");
            if (!File.Exists(file)) continue;
            // An existing SKILL.md that can't be read or parsed is an error, not a skill to drop: dropping it would send the
            // Controller a message missing that skill. The error surfaces through the poll's "controller update" message.
            var text = File.ReadAllText(file, Encoding.UTF8);
            if (!FrontMatter.TryParse(text, out var fields, out var body))
                throw new InvalidDataException($"{file} has no front matter (--- name/description ---).");
            if (fields.GetValueOrDefault("kind") != Kind) continue;
            skills.Add(new HubSkill(fields.GetValueOrDefault("name", Path.GetFileName(sub)), fields.GetValueOrDefault("description", ""), body));
        }
        return skills.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
    }
}

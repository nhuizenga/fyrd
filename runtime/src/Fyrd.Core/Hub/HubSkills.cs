using System.Text;

namespace Fyrd.Core.Hub;

public sealed record HubSkill(string Name, string Description, string Body);

/// <summary>
/// The Controller skills under <c>skills/controller/*/SKILL.md</c>. They are read from disk on every call (the files are
/// small), so a <c>git pull</c> reaches the Controller without a restart.
/// </summary>
public sealed class HubSkills(string dir)
{
    /// <summary>
    /// Skills ordered by name; empty when the directory is missing. Front matter is simple single-line <c>key: value</c> pairs
    /// (see <see cref="FrontMatter"/>). Throws
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
            skills.Add(new HubSkill(fields.GetValueOrDefault("name", Path.GetFileName(sub)), fields.GetValueOrDefault("description", ""), body));
        }
        return skills.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
    }
}

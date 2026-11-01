using System.Text;
using System.Text.RegularExpressions;

namespace Fyrd.Core.Hub;

public sealed record HubSkill(string Name, string Description, string Body);

/// <summary>
/// The Controller skills under <c>skills/controller/*/SKILL.md</c>. They are read from disk on every call (the files are
/// small), so a <c>git pull</c> reaches the Controller without a restart.
/// </summary>
public sealed partial class HubSkills(string dir)
{
    [GeneratedRegex(@"\A---[ \t]*\r?\n(?<fm>.*?)\r?\n---[ \t]*\r?\n?(?<body>.*)\z", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex FrontMatterRegex();

    /// <summary>Skills ordered by name; empty when the directory is missing. A SKILL.md without front matter is skipped.</summary>
    public IReadOnlyList<HubSkill> List()
    {
        if (!Directory.Exists(dir)) return [];
        var skills = new List<HubSkill>();
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            var file = Path.Combine(sub, "SKILL.md");
            if (!File.Exists(file)) continue;
            string text;
            try { text = File.ReadAllText(file, Encoding.UTF8); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            var m = FrontMatterRegex().Match(text);
            if (!m.Success) continue;
            string name = Path.GetFileName(sub), description = "";
            foreach (var line in m.Groups["fm"].Value.Split('\n'))
            {
                var i = line.IndexOf(':');
                if (i < 0) continue;
                var key = line[..i].Trim();
                var val = line[(i + 1)..].Trim().Trim('"');
                if (key == "name") name = val;
                else if (key == "description") description = val;
            }
            skills.Add(new HubSkill(name, description, m.Groups["body"].Value.Trim()));
        }
        return skills.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
    }
}

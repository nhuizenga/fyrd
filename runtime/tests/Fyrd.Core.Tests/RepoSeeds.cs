using Fyrd.Core.Hub;

namespace Fyrd.Core.Tests;

/// <summary>The checked-in seeds: the hub personas and the session skills, loaded through their stores into a temp home.</summary>
internal static class RepoSeeds
{
    public static string Dir(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine([dir.FullName, .. parts]);
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException(string.Join('/', parts) + " not found above " + AppContext.BaseDirectory);
    }

    public static IReadOnlyList<HubPersona> Personas(FyrdPaths paths) => new HubPersonas(paths, Dir("skills")).List();

    public static IReadOnlyList<SessionSkill> SessionSkills(FyrdPaths paths) => new SessionSkills(paths, Dir("skills")).List();

    /// <summary>
    /// Everything a session of this role is told: the persona with its session skills' bodies, as a session gets it
    /// when the plugin setup script is not confirmed. A rule may sit in the persona or in one of its skills.
    /// </summary>
    public static string Whole(FyrdPaths paths, string id) =>
        Hub.SessionSkills.Compose(Personas(paths).Single(p => p.Id == id), SessionSkills(paths), installed: false);
}

/// <summary>Writes a persona seed the way the repo holds one: <c>&lt;dir&gt;/&lt;id&gt;/SKILL.md</c>, a persona skill of the fyrd plugin.</summary>
internal static class PersonaSeedFile
{
    public static string Text(string id, string name, string model, bool locked, string body, string newline = "\n") =>
        string.Join(newline, "---", $"name: {id}", $"description: \"Fyrd persona: the {name}.\"", "disable-model-invocation: true", "kind: persona",
            $"persona-name: {name}", $"persona-model: {model}", $"persona-locked: {(locked ? "true" : "false")}", "---", body);

    public static void Write(string dir, string id, string name, string model, bool locked, string body)
    {
        Directory.CreateDirectory(Path.Combine(dir, id));
        File.WriteAllText(Path.Combine(dir, id, "SKILL.md"), Text(id, name, model, locked, body));
    }
}

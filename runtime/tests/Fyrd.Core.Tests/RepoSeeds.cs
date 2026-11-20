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

    public static IReadOnlyList<HubPersona> Personas(FyrdPaths paths) => new HubPersonas(paths, Dir("personas", "hub")).List();

    public static IReadOnlyList<SessionSkill> SessionSkills(FyrdPaths paths) => new SessionSkills(paths, Dir("skills")).List();

    /// <summary>
    /// Everything a session of this role is told: the persona with its session skills' bodies, as a session gets it
    /// when the plugin setup script is not confirmed. A rule may sit in the persona or in one of its skills.
    /// </summary>
    public static string Whole(FyrdPaths paths, string id) =>
        Hub.SessionSkills.Compose(Personas(paths).Single(p => p.Id == id), SessionSkills(paths), installed: false);
}

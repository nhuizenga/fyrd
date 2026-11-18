using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fyrd.Core.Config;

namespace Fyrd.Core.Hub;

/// <summary>
/// A skill a cloud session loads on demand with the Skill tool: part of a role's instructions that is needed only in
/// some situations. <c>Description</c> is one line saying what the skill holds and when to load it, <c>Roles</c> are the
/// persona ids it belongs to, and <c>Body</c> is the markdown. <c>SeedHash</c> is the <see cref="SessionSkills.ContentHash"/> of the seed this skill was
/// last synced from (null for one the owner added); <c>UpdateAvailable</c> is computed on read and never stored.
/// </summary>
public sealed record SessionSkill(string Name, string Description, IReadOnlyList<string> Roles, string Body, string? SeedHash = null, bool UpdateAvailable = false);

/// <summary>How the sessions of a role get their session skills.</summary>
public enum SessionSkillMode
{
    /// <summary>No setup script is confirmed: each skill's whole body is part of the persona text.</summary>
    Inline,

    /// <summary>The confirmed setup script matches the store: the persona text points at each skill.</summary>
    Installed,

    /// <summary>A setup script was confirmed, but the skills changed since: bodies are inline until the new script is confirmed.</summary>
    OutOfDate,
}

/// <summary>
/// Session skill store: one JSON file per skill under <see cref="FyrdPaths.HubSessionSkillsDir"/>, seeded from
/// <c>skills/session/&lt;name&gt;/SKILL.md</c> the way <see cref="HubPersonas"/> seeds personas: a seed is written once, an
/// unedited skill follows its seed (and is removed when its seed is), an edited one is left alone and flagged, and a
/// deleted one stays deleted.
/// </summary>
public sealed partial class SessionSkills(FyrdPaths paths, string seedDir)
{
    /// <summary>The most words a skill body may have. After compaction a session keeps only the first 5,000 tokens of a loaded skill.</summary>
    public const int MaxWords = 1500;

    /// <summary>Claude Code's own limits for a skill's name and description.</summary>
    public const int MaxNameLength = 64, MaxDescriptionLength = 1024;

    private const string SeededMarker = ".seeded";
    private static readonly string[] ReservedRoles = ["controller", HubMemory.Shared];
    private readonly object _gate = new();
    private bool _seeded;
    private Dictionary<string, SessionSkill> _seeds = new();

    [GeneratedRegex(@"^fyrd-[a-z0-9]+(?:-[a-z0-9]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex NameRegex();

    [GeneratedRegex(@"^[a-z0-9-]{1,40}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RoleRegex();

    /// <summary>Every stored skill, ordered by name.</summary>
    public IReadOnlyList<SessionSkill> List()
    {
        lock (_gate)
        {
            SeedIfNeeded();
            var all = new List<SessionSkill>();
            foreach (var f in Directory.EnumerateFiles(paths.HubSessionSkillsDir, "*.json"))
            {
                var s = Read(f);
                if (s is not null) all.Add(Flag(s));
            }
            return all.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
        }
    }

    public SessionSkill? Get(string name)
    {
        if (!IsValidName(name)) return null;
        lock (_gate)
        {
            SeedIfNeeded();
            return Read(FileFor(name)) is { } s ? Flag(s) : null;
        }
    }

    /// <summary>Creates or replaces a skill after <see cref="Normalize"/>. Throws <see cref="ArgumentException"/> for an invalid one.</summary>
    public SessionSkill Save(SessionSkill skill)
    {
        var valid = Normalize(skill);
        lock (_gate)
        {
            SeedIfNeeded();
            var saved = valid with { SeedHash = Read(FileFor(valid.Name))?.SeedHash, UpdateAvailable = false };
            Write(saved);
            return Flag(saved);
        }
    }

    /// <summary>
    /// Replaces a skill with its current seed. Reset is an explicit owner action, so it also recreates a skill the owner
    /// deleted. Throws <see cref="KeyNotFoundException"/> when the skill has no seed.
    /// </summary>
    public SessionSkill Reset(string name)
    {
        if (!IsValidName(name)) throw new ArgumentException($"Invalid session skill name '{name}'.", nameof(name));
        lock (_gate)
        {
            SeedIfNeeded();
            if (!_seeds.TryGetValue(name, out var seed)) throw new KeyNotFoundException($"Session skill '{name}' has no default to reset to.");
            var reset = seed with { SeedHash = ContentHash(seed) };
            Write(reset);
            return reset;
        }
    }

    /// <summary>False when no such skill exists.</summary>
    public bool Delete(string name)
    {
        if (!IsValidName(name)) throw new ArgumentException($"Invalid session skill name '{name}'.", nameof(name));
        lock (_gate)
        {
            SeedIfNeeded();
            if (!File.Exists(FileFor(name))) return false;
            File.Delete(FileFor(name));
            return true;
        }
    }

    /// <summary>The seeds that have no skill in the store (the owner deleted it), by name. <see cref="Reset"/> brings one back.</summary>
    public IReadOnlyList<string> MissingSeeds()
    {
        lock (_gate)
        {
            SeedIfNeeded();
            return _seeds.Keys.Where(name => !File.Exists(FileFor(name))).Order(StringComparer.Ordinal).ToList();
        }
    }

    public static bool IsValidName(string? name) => name is { Length: <= MaxNameLength } && NameRegex().IsMatch(name);

    public static int WordCount(string? text) => (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>
    /// The skill as it is stored: description and body trimmed, line ends as <c>\n</c>, roles distinct in the order
    /// given. Throws <see cref="ArgumentException"/> when the name is not <c>fyrd-</c> followed by lower-case words joined
    /// by hyphens, the description is empty or not one line, no role is given or one is not a peasant persona id, or the
    /// body is empty, has more than <see cref="MaxWords"/> words or holds a control character.
    /// </summary>
    public static SessionSkill Normalize(SessionSkill skill)
    {
        if (!IsValidName(skill.Name))
            throw new ArgumentException($"A session skill's name is 'fyrd-' followed by lower-case letters, digits and single hyphens, at most {MaxNameLength} characters.", nameof(skill));
        var description = (skill.Description ?? "").Trim();
        if (description.Length == 0) throw new ArgumentException("Description is required: one line saying what the skill holds and when to load it.", nameof(skill));
        // U+2028 and U+2029 end a line too, though they are no control characters (U+0085 is one).
        if (description.Any(c => char.IsControl(c) || c is '\u2028' or '\u2029')) throw new ArgumentException("Description must be one line.", nameof(skill));
        if (description.Length > MaxDescriptionLength) throw new ArgumentException($"Description is longer than {MaxDescriptionLength} characters.", nameof(skill));
        var roles = (skill.Roles ?? []).Select(r => (r ?? "").Trim()).Where(r => r.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (roles.Count == 0) throw new ArgumentException("A session skill belongs to at least one role.", nameof(skill));
        foreach (var role in roles)
            if (!RoleRegex().IsMatch(role) || ReservedRoles.Contains(role))
                throw new ArgumentException($"'{role}' is not a role a session skill can belong to.", nameof(skill));
        var body = (skill.Body ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (body.Length == 0) throw new ArgumentException("Body is required.", nameof(skill));
        if (body.Any(c => char.IsControl(c) && c != '\n' && c != '\t')) throw new ArgumentException("Body must not contain control characters.", nameof(skill));
        var words = WordCount(body);
        if (words > MaxWords) throw new ArgumentException($"Body has {words} words; a session skill holds at most {MaxWords}.", nameof(skill));
        return new SessionSkill(skill.Name, description, roles, body, skill.SeedHash);
    }

    /// <summary>
    /// SHA-256 (lowercase hex) of one skill's name, description, roles and body, and of nothing else. It is what a
    /// stored skill's <c>SeedHash</c> holds, and what tells an unedited skill from an edited one, so it must stay the
    /// same for the same text for good: <see cref="SessionSkillScript.TemplateVersion"/> is no part of it.
    /// </summary>
    public static string ContentHash(SessionSkill skill) => Hash("fyrd-session-skill", [skill]);

    /// <summary>
    /// The template versions whose <see cref="Hash(IEnumerable{SessionSkill})"/> of one skill was once stored as its
    /// <c>SeedHash</c>. Stores written then still hold those; they are read as the content hash and rewritten.
    /// </summary>
    private static readonly int[] LegacySeedHashVersions = [1, 2];

    /// <summary>Whether <paramref name="seedHash"/> is the seed hash of this text, in today's form or an earlier one.</summary>
    private static bool IsSeedHashOf(string? seedHash, SessionSkill text) =>
        seedHash is not null
        && (seedHash == ContentHash(text) || LegacySeedHashVersions.Any(v => seedHash == Hash($"fyrd-session-skills-{v}", [text])));

    /// <summary>
    /// SHA-256 (lowercase hex) of a set of skills: the same for the same skills in any order, and different when a name,
    /// description, role or body differs. It names the setup script generated from them. The roles are part of it
    /// although the script doesn't hold them: a role change moves which personas point at the skill. So is
    /// <see cref="SessionSkillScript.TemplateVersion"/>: a block with other shell text is another script.
    /// </summary>
    public static string Hash(IEnumerable<SessionSkill> skills) => Hash($"fyrd-session-skills-{SessionSkillScript.TemplateVersion}", skills);

    private static string Hash(string prefix, IEnumerable<SessionSkill> skills)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartArray();
            json.WriteStringValue(prefix);
            foreach (var s in skills.OrderBy(s => s.Name, StringComparer.Ordinal))
            {
                json.WriteStartArray();
                json.WriteStringValue(s.Name);
                json.WriteStringValue(s.Description);
                json.WriteStartArray();
                foreach (var role in s.Roles.Order(StringComparer.Ordinal)) json.WriteStringValue(role);
                json.WriteEndArray();
                json.WriteStringValue(s.Body);
                json.WriteEndArray();
            }
            json.WriteEndArray();
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    /// <summary>
    /// Installed only when the owner confirmed a setup script and its hash is the hash of these skills; any change to a
    /// skill since then is <see cref="SessionSkillMode.OutOfDate"/>, which sessions get as inline text.
    /// </summary>
    public static SessionSkillMode Mode(FyrdConfig config, IReadOnlyList<SessionSkill> skills) =>
        string.IsNullOrEmpty(config.SessionSkillsHash) ? SessionSkillMode.Inline
        : string.Equals(config.SessionSkillsHash, Hash(skills), StringComparison.Ordinal) ? SessionSkillMode.Installed
        : SessionSkillMode.OutOfDate;

    /// <summary>
    /// The persona text a session of this persona's role gets: the stored definition, then its role's session skills.
    /// Installed, each skill is one line that says when to load it, under an intro that carries the hash of
    /// <paramref name="skills"/> (all of them, as the setup script has it) for the session to check; otherwise each skill's whole body is a section, so
    /// the session has every rule whether or not the setup script ran. A persona without session skills is unchanged.
    /// </summary>
    public static string Compose(HubPersona persona, IReadOnlyList<SessionSkill>? skills, bool installed)
    {
        var own = (skills ?? []).Where(s => s.Roles.Contains(persona.Id)).OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
        if (own.Count == 0) return persona.Definition;
        var parts = new List<string> { persona.Definition };
        if (installed)
            parts.Add(PointerHeading + "\n\n" + PointerIntro(Hash(skills!)) + "\n\n" + string.Join("\n", own.Select(Pointer)));
        else
            parts.AddRange(own.Select(Section));
        return string.Join("\n\n", parts.Where(p => p.Length > 0));
    }

    public const string PointerHeading = "# Session skills";

    /// <summary>
    /// Said once for every line under it: load the skill first, check once that the environment holds the skills this
    /// text was made for (the setup script writes their hash last, and only after a complete run), and never go on
    /// without them. <paramref name="hash"/> is the hash of all the skills, which is the confirmed one in installed mode.
    /// </summary>
    public static string PointerIntro(string hash) =>
        "Some sections of your instructions are skills in your session, not text here. Before you act on what a line below covers, load its skill with the Skill tool. "
        + $"Before the first load, check once that the file `{SessionSkillScript.HashFile}` holds one line, exactly `{hash}`. "
        + "If the file is missing or holds anything else, or the skill is not in your skill list, stop and ask the owner.";

    /// <summary>The line that stands for an installed skill in a persona: its name and its description (what it holds and when to load it).</summary>
    public static string Pointer(SessionSkill skill) => $"- `{skill.Name}`: {skill.Description}";

    /// <summary>A skill's body as a section of a persona; a body that doesn't open with a heading gets its name as one.</summary>
    private static string Section(SessionSkill skill) =>
        skill.Body.StartsWith("# ", StringComparison.Ordinal) ? skill.Body : $"# {skill.Name}\n\n{skill.Body}";

    private SessionSkill Flag(SessionSkill s)
    {
        if (!_seeds.TryGetValue(s.Name, out var seed)) return s with { UpdateAvailable = false };
        return s with { UpdateAvailable = ContentHash(s) != ContentHash(seed) && !IsSeedHashOf(s.SeedHash, seed) };
    }

    private string FileFor(string name) => Path.Combine(paths.HubSessionSkillsDir, name + ".json");

    private void Write(SessionSkill s) =>
        AtomicFile.Write(FileFor(s.Name), JsonSerializer.Serialize(s with { UpdateAvailable = false }, Json.Options));

    private static SessionSkill? Read(string file)
    {
        if (!File.Exists(file)) return null;
        try
        {
            var s = JsonSerializer.Deserialize<SessionSkill>(File.ReadAllText(file), Json.Options);
            // A file edited by hand into something the store would refuse is not a skill.
            return s is null ? null : Normalize(s);
        }
        catch (Exception e) when (e is JsonException or ArgumentException) { return null; }
    }

    /// <summary>
    /// A seed is <c>SKILL.md</c> with <c>name</c>, <c>description</c> and <c>roles</c> (comma-separated persona ids) in
    /// its front matter. Null when the file has no front matter, its name is not its directory's, or it is not a valid skill.
    /// </summary>
    public static SessionSkill? ParseSeed(string directoryName, string text)
    {
        if (!FrontMatter.TryParse(text, out var fields, out var body)) return null;
        var name = fields.GetValueOrDefault("name", directoryName);
        var description = fields.GetValueOrDefault("description", "");
        var roles = fields.GetValueOrDefault("roles", "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (name != directoryName) return null;
        try { return Normalize(new SessionSkill(name, description, roles, body)); }
        catch (ArgumentException) { return null; }
    }

    /// <summary>
    /// Seeds skills that were never seeded, and brings unedited ones up to the current seed once per process. A skill
    /// counts as unedited when its <c>SeedHash</c> is the seed hash of its own text (<see cref="IsSeedHashOf"/>, which
    /// also knows the earlier forms; a seed hash in one of those is rewritten as the content hash). Every stored skill
    /// has had a <c>SeedHash</c> since the store began, so there is no history file as the personas have.
    /// </summary>
    private void SeedIfNeeded()
    {
        if (_seeded) return;
        _seeded = true;
        Directory.CreateDirectory(paths.HubSessionSkillsDir);
        if (!Directory.Exists(seedDir)) return;
        var marker = Path.Combine(paths.HubSessionSkillsDir, SeededMarker);
        var seededNames = File.Exists(marker)
            ? File.ReadAllLines(marker).Select(l => l.Trim()).Where(l => l.Length > 0).ToHashSet()
            : new HashSet<string>();
        var markerChanged = false;
        foreach (var dir in Directory.EnumerateDirectories(seedDir))
        {
            var name = Path.GetFileName(dir);
            var file = Path.Combine(dir, "SKILL.md");
            if (!IsValidName(name) || !File.Exists(file)) continue;
            var seed = ParseSeed(name, File.ReadAllText(file, Encoding.UTF8));
            if (seed is null) continue;
            _seeds[name] = seed;
            var seedHash = ContentHash(seed);
            var stored = Read(FileFor(name));
            if (!seededNames.Contains(name))
            {
                seededNames.Add(name);
                markerChanged = true;
                if (stored is null)
                {
                    Write(seed with { SeedHash = seedHash });
                    continue;
                }
            }
            if (stored is null) continue; // deleted by the owner: stays deleted
            if (stored.SeedHash == seedHash) continue; // synced with this seed, edited since or not
            // Unedited: it follows the seed.
            if (IsSeedHashOf(stored.SeedHash, stored)) Write(seed with { SeedHash = seedHash });
            // Edited after a sync with this very seed, recorded in an earlier form: only the record changes.
            else if (IsSeedHashOf(stored.SeedHash, seed)) Write(stored with { SeedHash = seedHash });
            // Otherwise edited, or one the owner made under a name a seed took later: leave it alone.
        }
        // A seed that is gone: a skill that was seeded from it and never edited goes with it, as an unedited skill
        // follows its seed; an edited one is the owner's text and stays, as a skill of their own.
        foreach (var name in seededNames.Where(n => !_seeds.ContainsKey(n)).ToList())
        {
            // Only a seed whose file is gone; one that is there but can't be read as a skill removes nothing.
            if (!IsValidName(name) || File.Exists(Path.Combine(seedDir, name, "SKILL.md"))) continue;
            var stored = Read(FileFor(name));
            if (stored is not null && !IsSeedHashOf(stored.SeedHash, stored)) continue;
            if (stored is not null) File.Delete(FileFor(name));
            seededNames.Remove(name);
            markerChanged = true;
        }
        if (markerChanged) AtomicFile.Write(marker, string.Join("\n", seededNames.Order(StringComparer.Ordinal)) + "\n");
    }
}

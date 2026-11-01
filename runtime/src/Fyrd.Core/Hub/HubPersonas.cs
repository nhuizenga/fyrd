using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fyrd.Core.Hub;

/// <summary>
/// <c>SeedHash</c> is the hash of the seed text this persona was last synced from (null for legacy installs).
/// <c>UpdateAvailable</c> is computed on read and never stored.
/// </summary>
public sealed record HubPersona(string Id, string Name, string Model, string Definition, bool Locked, string? SeedHash = null, bool UpdateAvailable = false);

/// <summary>Hub persona store: one JSON file per persona under <see cref="FyrdPaths.HubPersonasDir"/>, seeded from markdown files.</summary>
public sealed partial class HubPersonas(FyrdPaths paths, string seedDir)
{
    public static readonly IReadOnlyList<string> AllowedModels =
        ["claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5", "claude-fable-5-1"];

    private const string ControllerId = "controller";
    private const string SeededMarker = ".seeded";
    private readonly object _gate = new();
    private const string HistoryFile = "seed-history.json";
    private bool _seeded;
    private Dictionary<string, HubPersona> _seeds = new();

    [GeneratedRegex("^[a-z0-9-]{1,40}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdRegex();

    [GeneratedRegex(@"\A---[ \t]*\r?\n(?<fm>.*?)\r?\n---[ \t]*\r?\n?(?<body>.*)\z", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex FrontMatterRegex();

    public IReadOnlyList<HubPersona> List()
    {
        lock (_gate)
        {
            SeedIfNeeded();
            var all = new List<HubPersona>();
            foreach (var f in Directory.EnumerateFiles(paths.HubPersonasDir, "*.json"))
            {
                var p = Read(f);
                if (p is not null) all.Add(Flag(p));
            }
            return all
                .OrderBy(p => p.Id == ControllerId ? 0 : 1)
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public HubPersona? Get(string id)
    {
        if (!IdRegex().IsMatch(id ?? "")) return null;
        lock (_gate)
        {
            SeedIfNeeded();
            return Flag(Read(FileFor(id!)));
        }
    }

    /// <summary>
    /// Replaces a persona with its current seed text. Reset is an explicit owner action, so it also recreates a persona the
    /// owner deleted (sync alone never does). Throws <see cref="KeyNotFoundException"/> when the persona has no seed.
    /// </summary>
    public HubPersona Reset(string id)
    {
        lock (_gate)
        {
            SeedIfNeeded();
            Validate(id);
            if (!_seeds.TryGetValue(id, out var seed)) throw new KeyNotFoundException($"Persona '{id}' has no default to reset to.");
            var existing = Read(FileFor(id));
            var effective = Effective(seed, existing);
            var reset = effective with { Locked = existing?.Locked ?? seed.Locked, SeedHash = Hash(effective) };
            AtomicFile.Write(FileFor(id), JsonSerializer.Serialize(reset, Json.Options));
            return reset;
        }
    }

    /// <summary>SHA-256 (lowercase hex) of name, model and definition. <c>scripts/seed-history.mjs</c> computes the same value.</summary>
    public static string Hash(HubPersona p) => Hash(p.Name, p.Model, p.Definition);

    public static string Hash(string name, string model, string definition) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name + "\n" + model + "\n" + definition))).ToLowerInvariant();

    /// <summary>A locked persona keeps its stored name, so its seed is compared and hashed under that name.</summary>
    private static HubPersona Effective(HubPersona seed, HubPersona? stored) =>
        stored is { Locked: true } ? seed with { Name = stored.Name } : seed;

    private HubPersona? Flag(HubPersona? p)
    {
        if (p is null) return null;
        if (!_seeds.TryGetValue(p.Id, out var seed)) return p with { UpdateAvailable = false };
        var own = Hash(p);
        var seedHash = Hash(Effective(seed, p));
        return p with { UpdateAvailable = own != seedHash && p.SeedHash != seedHash };
    }

    public HubPersona Save(HubPersona p)
    {
        if (!AllowedModels.Contains(p.Model)) throw new ArgumentException($"Unknown model '{p.Model}'.", nameof(p));
        if (string.IsNullOrWhiteSpace(p.Name)) throw new ArgumentException("Name is required.", nameof(p));
        lock (_gate)
        {
            SeedIfNeeded();
            HubPersona saved;
            var existing = string.IsNullOrEmpty(p.Id) ? null : Read(FileFor(Validate(p.Id)));
            if (existing is not null)
            {
                // Locked personas (the Controller) keep their name; Fyrd and the skills refer to them by it.
                if (existing.Locked && !string.Equals(existing.Name, p.Name.Trim(), StringComparison.Ordinal))
                    throw new InvalidOperationException($"The {existing.Name} persona can't be renamed.");
                saved = existing with { Name = p.Name.Trim(), Model = p.Model, Definition = p.Definition ?? "", UpdateAvailable = false };
            }
            else
            {
                var id = string.IsNullOrEmpty(p.Id) ? Slug(p.Name) : Validate(p.Id);
                if (id == HubMemory.Shared) throw new ArgumentException($"'{HubMemory.Shared}' is a reserved persona id.", nameof(p));
                if (File.Exists(FileFor(id))) throw new InvalidOperationException($"A persona with id '{id}' already exists.");
                saved = new HubPersona(id, p.Name.Trim(), p.Model, p.Definition ?? "", p.Locked);
            }
            AtomicFile.Write(FileFor(saved.Id), JsonSerializer.Serialize(saved with { UpdateAvailable = false }, Json.Options));
            return Flag(saved)!;
        }
    }

    /// <summary>False when no such persona exists.</summary>
    public bool Delete(string id)
    {
        lock (_gate)
        {
            SeedIfNeeded();
            var existing = Read(FileFor(Validate(id)));
            if (existing is null) return false;
            if (existing.Locked) throw new InvalidOperationException($"Persona '{id}' is locked and cannot be deleted.");
            File.Delete(FileFor(id));
            return true;
        }
    }

    public static string Slug(string name)
    {
        var s = Regex.Replace((name ?? "").Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (s.Length > 40) s = s[..40].Trim('-');
        if (s.Length == 0) throw new ArgumentException("Name must contain letters or digits.", nameof(name));
        return s;
    }

    private static string Validate(string id) =>
        IdRegex().IsMatch(id ?? "") ? id! : throw new ArgumentException($"Invalid persona id '{id}'.", nameof(id));

    private string FileFor(string id) => Path.Combine(paths.HubPersonasDir, id + ".json");

    private static HubPersona? Read(string file)
    {
        if (!File.Exists(file)) return null;
        try { return JsonSerializer.Deserialize<HubPersona>(File.ReadAllText(file), Json.Options); }
        catch (JsonException) { return null; }
    }

    private static HubPersona? ParseSeed(string file)
    {
        var id = Path.GetFileNameWithoutExtension(file);
        var m = FrontMatterRegex().Match(File.ReadAllText(file, Encoding.UTF8));
        if (!m.Success) return null;
        string name = id, model = "claude-sonnet-5-5";
        var locked = false;
        foreach (var line in m.Groups["fm"].Value.Split('\n'))
        {
            var i = line.IndexOf(':');
            if (i < 0) continue;
            var key = line[..i].Trim();
            var val = line[(i + 1)..].Trim();
            if (key == "name") name = val;
            else if (key == "model") model = val;
            else if (key == "locked") locked = val.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        if (!AllowedModels.Contains(model)) return null;
        // Normalised so a CRLF checkout hashes the same as the LF text seed-history.json was built from.
        return new HubPersona(id, name, model, m.Groups["body"].Value.Replace("\r\n", "\n").Trim(), locked);
    }

    private Dictionary<string, HashSet<string>> ReadHistory()
    {
        var file = Path.Combine(seedDir, HistoryFile);
        try
        {
            if (File.Exists(file))
                return (JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(file), Json.Options) ?? new())
                    .ToDictionary(kv => kv.Key, kv => kv.Value.ToHashSet(StringComparer.OrdinalIgnoreCase));
        }
        catch (JsonException) { }
        return new();
    }

    /// <summary>
    /// Seeds personas that were never seeded, and brings unedited ones up to the current seed text once per process.
    /// A persona counts as unedited when its own hash equals the hash of the seed it came from.
    /// </summary>
    private void SeedIfNeeded()
    {
        if (_seeded) return;
        _seeded = true;
        Directory.CreateDirectory(paths.HubPersonasDir);
        if (!Directory.Exists(seedDir)) return;
        var marker = Path.Combine(paths.HubPersonasDir, SeededMarker);
        var seededIds = File.Exists(marker)
            ? File.ReadAllLines(marker).Select(l => l.Trim()).Where(l => l.Length > 0).ToHashSet()
            : new HashSet<string>();
        var markerChanged = false;
        var history = ReadHistory();
        foreach (var f in Directory.EnumerateFiles(seedDir, "*.md"))
        {
            var id = Path.GetFileNameWithoutExtension(f);
            if (!IdRegex().IsMatch(id) || id == HubMemory.Shared) continue;
            var seed = ParseSeed(f);
            if (seed is null) continue;
            _seeds[id] = seed;
            var stored = Read(FileFor(id));
            var seedHash = Hash(Effective(seed, stored));
            if (!seededIds.Contains(id))
            {
                seededIds.Add(id);
                markerChanged = true;
                if (stored is null)
                {
                    AtomicFile.Write(FileFor(id), JsonSerializer.Serialize(seed with { SeedHash = seedHash }, Json.Options));
                    continue;
                }
            }
            if (stored is null) continue; // deleted by the owner: stays deleted
            var own = Hash(stored);
            var origin = stored.SeedHash;
            if (origin is null && history.TryGetValue(id, out var known) && known.Contains(own)) origin = own;
            if (origin is null || own != origin)
            {
                // Edited, or a legacy persona we can't place: leave the text alone.
                continue;
            }
            if (origin == seedHash)
            {
                if (stored.SeedHash != origin) Write(stored with { SeedHash = origin });
                continue;
            }
            Write(Effective(seed, stored) with { Locked = stored.Locked, SeedHash = seedHash });
        }
        if (markerChanged) AtomicFile.Write(marker, string.Join("\n", seededIds.Order(StringComparer.Ordinal)) + "\n");

        void Write(HubPersona p) => AtomicFile.Write(FileFor(p.Id), JsonSerializer.Serialize(p, Json.Options));
    }
}

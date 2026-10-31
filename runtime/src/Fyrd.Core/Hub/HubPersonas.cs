using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fyrd.Core.Hub;

public sealed record HubPersona(string Id, string Name, string Model, string Definition, bool Locked);

/// <summary>Hub persona store: one JSON file per persona under <see cref="FyrdPaths.HubPersonasDir"/>, seeded from markdown files.</summary>
public sealed partial class HubPersonas(FyrdPaths paths, string seedDir)
{
    public static readonly IReadOnlyList<string> AllowedModels =
        ["claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5", "claude-fable-5-1"];

    private const string ControllerId = "controller";
    private const string SeededMarker = ".seeded";
    private readonly object _gate = new();
    private bool _seeded;

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
                if (p is not null) all.Add(p);
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
            return Read(FileFor(id!));
        }
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
                saved = existing with { Name = p.Name.Trim(), Model = p.Model, Definition = p.Definition ?? "" };
            }
            else
            {
                var id = string.IsNullOrEmpty(p.Id) ? Slug(p.Name) : Validate(p.Id);
                if (id == HubMemory.Shared) throw new ArgumentException($"'{HubMemory.Shared}' is a reserved persona id.", nameof(p));
                if (File.Exists(FileFor(id))) throw new InvalidOperationException($"A persona with id '{id}' already exists.");
                saved = new HubPersona(id, p.Name.Trim(), p.Model, p.Definition ?? "", p.Locked);
            }
            AtomicFile.Write(FileFor(saved.Id), JsonSerializer.Serialize(saved, Json.Options));
            return saved;
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
        foreach (var f in Directory.EnumerateFiles(seedDir, "*.md"))
        {
            var id = Path.GetFileNameWithoutExtension(f);
            if (!IdRegex().IsMatch(id) || id == HubMemory.Shared || seededIds.Contains(id)) continue;
            seededIds.Add(id);
            markerChanged = true;
            if (File.Exists(FileFor(id))) continue;
            var m = FrontMatterRegex().Match(File.ReadAllText(f, Encoding.UTF8));
            if (!m.Success) continue;
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
            if (!AllowedModels.Contains(model)) continue;
            var seeded = new HubPersona(id, name, model, m.Groups["body"].Value.Trim(), locked);
            AtomicFile.Write(FileFor(id), JsonSerializer.Serialize(seeded, Json.Options));
        }
        if (markerChanged) AtomicFile.Write(marker, string.Join("\n", seededIds.Order(StringComparer.Ordinal)) + "\n");
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fyrd.Core.Hub;

/// <summary>
/// A hub persona: everything but <c>Model</c> comes from its seed in the repo. <c>Model</c> is the owner's override, or
/// the seed's model (<c>DefaultModel</c>) when there is none.
/// </summary>
public sealed record HubPersona(string Id, string Name, string Model, string Definition, bool Locked, string? DefaultModel = null);

/// <summary>
/// Hub personas, read from the persona skills of the fyrd plugin: <c>skills/&lt;id&gt;/SKILL.md</c> whose front matter
/// says <c>kind: persona</c>. The skill's body is the definition, and <c>persona-name</c>, <c>persona-model</c> and
/// <c>persona-locked</c> are the rest. The same file is the plugin skill <c>fyrd:&lt;id&gt;</c>, so the persona text has
/// one copy. The only thing the owner changes is a persona's model: the override is <c>{ "model": … }</c> in
/// <c>&lt;id&gt;.json</c> under <see cref="FyrdPaths.HubPersonasDir"/>, and the name and text always follow the repo.
/// <para>
/// Older installs stored the whole persona there. Such a file is read for its model only, and only when it was edited
/// (its hash is neither its <c>seedHash</c> nor in <c>persona-seed-history.json</c>); an unedited one followed its seed
/// and so has no override. Its other fields, and files of personas that have no seed (custom personas), are ignored.
/// </para>
/// </summary>
public sealed partial class HubPersonas(FyrdPaths paths, string seedDir)
{
    public static readonly IReadOnlyList<string> AllowedModels =
        ["claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5", "claude-fable-5-2", "claude-fable-5-1"];

    private const string ControllerId = "controller";
    private const string HistoryFile = "persona-seed-history.json";
    private readonly object _gate = new();

    /// <summary>The front matter <c>kind</c> of a persona skill.</summary>
    public const string Kind = "persona";
    private Dictionary<string, HubPersona>? _seeds;
    private Dictionary<string, HashSet<string>>? _history;

    [GeneratedRegex("^[a-z0-9-]{1,40}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdRegex();

    /// <summary>What an override file holds. <c>Name</c>, <c>Definition</c> and <c>SeedHash</c> are only in older files.</summary>
    private sealed record Stored(string? Model, string? Name = null, string? Definition = null, string? SeedHash = null);

    private sealed record Override(string Model);

    public IReadOnlyList<HubPersona> List()
    {
        lock (_gate)
        {
            return Seeds().Values.Select(WithOverride)
                .OrderBy(p => p.Id == ControllerId ? 0 : 1)
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public HubPersona? Get(string id)
    {
        if (!IdRegex().IsMatch(id ?? "")) return null;
        lock (_gate) return Seeds().TryGetValue(id!, out var seed) ? WithOverride(seed) : null;
    }

    /// <summary>
    /// Sets a persona's model. Choosing its default model clears the override, so the persona follows the repo's model
    /// again. Throws <see cref="KeyNotFoundException"/> for an unknown persona and <see cref="ArgumentException"/> for a
    /// model that isn't allowed.
    /// </summary>
    public HubPersona SetModel(string id, string model)
    {
        if (!AllowedModels.Contains(model)) throw new ArgumentException($"Unknown model '{model}'.", nameof(model));
        lock (_gate)
        {
            var seed = SeedFor(id);
            if (model == seed.Model) File.Delete(FileFor(id));
            else AtomicFile.Write(FileFor(id), JsonSerializer.Serialize(new Override(model), Json.Options));
            return WithOverride(seed);
        }
    }

    /// <summary>Clears a persona's model override. Throws <see cref="KeyNotFoundException"/> for an unknown persona.</summary>
    public HubPersona Reset(string id)
    {
        lock (_gate)
        {
            var seed = SeedFor(id);
            File.Delete(FileFor(id));
            return WithOverride(seed);
        }
    }

    /// <summary>SHA-256 (lowercase hex) of name, model and definition. <c>scripts/seed-history.mjs</c> computes the same value.</summary>
    public static string Hash(HubPersona p) => Hash(p.Name, p.Model, p.Definition);

    public static string Hash(string name, string model, string definition) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name + "\n" + model + "\n" + definition))).ToLowerInvariant();

    private HubPersona SeedFor(string id) =>
        Seeds().TryGetValue(Validate(id), out var seed) ? seed : throw new KeyNotFoundException($"Persona '{id}' not found.");

    private HubPersona WithOverride(HubPersona seed) =>
        OverrideModel(seed.Id) is { } model && model != seed.Model ? seed with { Model = model } : seed;

    /// <summary>The model the owner chose for this persona, or null (no file, an unreadable one, or an unedited older one).</summary>
    private string? OverrideModel(string id)
    {
        var file = FileFor(id);
        if (!File.Exists(file)) return null;
        Stored? stored;
        try { stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(file), Json.Options); }
        catch (JsonException) { return null; }
        if (stored?.Model is not { } model || !AllowedModels.Contains(model)) return null;
        if (stored.Definition is null) return model;
        // An older whole-persona file: an unedited one followed its seed, so its model is not the owner's choice.
        var own = Hash(stored.Name ?? "", model, stored.Definition);
        var unedited = own == stored.SeedHash || (History().TryGetValue(id, out var known) && known.Contains(own));
        return unedited ? null : model;
    }

    private static string Validate(string id) =>
        IdRegex().IsMatch(id ?? "") ? id! : throw new ArgumentException($"Invalid persona id '{id}'.", nameof(id));

    private string FileFor(string id) => Path.Combine(paths.HubPersonasDir, id + ".json");

    /// <summary>
    /// A persona seed is a <c>SKILL.md</c> whose front matter has <c>kind: persona</c> and a <c>name</c> equal to its
    /// directory's, the persona id. Null for any other skill, and for a persona whose model isn't allowed.
    /// </summary>
    public static HubPersona? ParseSeed(string id, string text)
    {
        if (!FrontMatter.TryParse(text, out var fields, out var body)) return null;
        if (fields.GetValueOrDefault("kind") != Kind || fields.GetValueOrDefault("name") != id) return null;
        var name = fields.GetValueOrDefault("persona-name", id);
        var model = fields.GetValueOrDefault("persona-model", "claude-sonnet-5-5");
        var locked = fields.GetValueOrDefault("persona-locked", "").Equals("true", StringComparison.OrdinalIgnoreCase);
        if (!AllowedModels.Contains(model)) return null;
        // Normalised so a CRLF checkout hashes the same as the LF text seed-history.json was built from.
        return new HubPersona(id, name, model, body, locked, model);
    }

    private Dictionary<string, HashSet<string>> History()
    {
        if (_history is not null) return _history;
        _history = new();
        var file = Path.Combine(seedDir, HistoryFile);
        try
        {
            if (File.Exists(file))
                _history = (JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(file), Json.Options) ?? new())
                    .ToDictionary(kv => kv.Key, kv => kv.Value.ToHashSet(StringComparer.OrdinalIgnoreCase));
        }
        catch (JsonException) { }
        return _history;
    }

    /// <summary>The persona seeds, read once per process.</summary>
    private Dictionary<string, HubPersona> Seeds()
    {
        if (_seeds is not null) return _seeds;
        _seeds = new();
        Directory.CreateDirectory(paths.HubPersonasDir);
        if (!Directory.Exists(seedDir)) return _seeds;
        foreach (var dir in Directory.EnumerateDirectories(seedDir))
        {
            var id = Path.GetFileName(dir);
            var file = Path.Combine(dir, "SKILL.md");
            if (!IdRegex().IsMatch(id) || id == HubMemory.Shared || !File.Exists(file)) continue;
            if (ParseSeed(id, File.ReadAllText(file, Encoding.UTF8)) is { } seed) _seeds[id] = seed;
        }
        return _seeds;
    }
}

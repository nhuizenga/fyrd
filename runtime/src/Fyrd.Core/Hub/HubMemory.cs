using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fyrd.Core.Hub;

public sealed record MemoryEntry(string Id, string Text, string Source, DateTimeOffset At);

/// <summary>Hub memory: one JSON array per scope ("shared" or a persona id) under <see cref="FyrdPaths.HubMemoryDir"/>.</summary>
public sealed partial class HubMemory(FyrdPaths paths)
{
    public const string Shared = "shared";

    private readonly object _gate = new();
    private long _version;

    [GeneratedRegex("^[a-z0-9-]{1,40}$", RegexOptions.CultureInvariant)]
    private static partial Regex ScopeRegex();

    public long Version { get { lock (_gate) return _version; } }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<MemoryEntry> List(string scope)
    {
        var file = FileFor(scope);
        lock (_gate) return Newest(Read(file));
    }

    public IReadOnlyDictionary<string, IReadOnlyList<MemoryEntry>> All()
    {
        var result = new Dictionary<string, IReadOnlyList<MemoryEntry>>();
        lock (_gate)
        {
            Directory.CreateDirectory(paths.HubMemoryDir);
            foreach (var f in Directory.EnumerateFiles(paths.HubMemoryDir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                var scope = Path.GetFileNameWithoutExtension(f);
                if (!ScopeRegex().IsMatch(scope)) continue;
                result[scope] = Newest(Read(f));
            }
        }
        return result;
    }

    public MemoryEntry Add(string scope, string text, string source, DateTimeOffset at)
    {
        var file = FileFor(scope);
        var entry = new MemoryEntry(Guid.NewGuid().ToString("N"), text, source, at);
        lock (_gate)
        {
            var entries = Read(file);
            entries.Add(entry);
            Write(file, entries);
        }
        return entry;
    }

    public void Update(string scope, string id, string text)
    {
        var file = FileFor(scope);
        lock (_gate)
        {
            var entries = Read(file);
            var i = entries.FindIndex(e => e.Id == id);
            if (i < 0) throw new KeyNotFoundException($"No memory entry '{id}' in scope '{scope}'.");
            entries[i] = entries[i] with { Text = text };
            Write(file, entries);
        }
    }

    public void Delete(string scope, string id)
    {
        var file = FileFor(scope);
        lock (_gate)
        {
            var entries = Read(file);
            if (entries.RemoveAll(e => e.Id == id) == 0)
                throw new KeyNotFoundException($"No memory entry '{id}' in scope '{scope}'.");
            Write(file, entries);
        }
    }

    /// <summary>"- text" lines, oldest first; empty string when there are no entries.</summary>
    public string Render(string scope) =>
        string.Join("\n", List(scope).Reverse().Select(e => "- " + e.Text));

    // Entries are appended oldest-to-newest; the stable sort keeps insertion order for equal timestamps.
    private static IReadOnlyList<MemoryEntry> Newest(List<MemoryEntry> entries)
    {
        var indexed = entries.Select((e, i) => (e, i)).OrderBy(x => x.e.At).ThenBy(x => x.i);
        return indexed.Select(x => x.e).Reverse().ToList();
    }

    private string FileFor(string scope)
    {
        if (!ScopeRegex().IsMatch(scope ?? "")) throw new ArgumentException($"Invalid memory scope '{scope}'.", nameof(scope));
        return Path.Combine(paths.HubMemoryDir, scope + ".json");
    }

    private static List<MemoryEntry> Read(string file)
    {
        if (!File.Exists(file)) return [];
        try { return JsonSerializer.Deserialize<List<MemoryEntry>>(File.ReadAllText(file), Json.Options) ?? []; }
        catch (JsonException) { return []; }
    }

    private void Write(string file, List<MemoryEntry> entries)
    {
        Directory.CreateDirectory(paths.HubMemoryDir);
        AtomicFile.Write(file, JsonSerializer.Serialize(entries, Json.Options));
        _version++;
    }
}

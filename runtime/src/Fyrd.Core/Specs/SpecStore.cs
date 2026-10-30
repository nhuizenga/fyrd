using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fyrd.Core.Specs;

public sealed partial class SpecStore(FyrdPaths paths, TimeProvider time)
{
    private const string RunFile = "run.json";
    private const string MarkdownFile = "spec.md";
    private const string UnitsFile = "units.json";

    public SpecRun Create(string title, string repo, string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            throw new ArgumentException("Spec markdown must not be empty.", nameof(markdown));
        if (repo is null || !RepoPattern().IsMatch(repo))
            throw new ArgumentException("Repo must look like 'owner/name'.", nameof(repo));

        var now = time.GetUtcNow();
        var baseId = SpecIds.NewId(title, now);
        var id = baseId;
        for (var n = 2; Directory.Exists(paths.SpecDir(id)); n++) id = $"{baseId}-{n}";
        Directory.CreateDirectory(paths.SpecDir(id));

        var run = new SpecRun(id, title, repo, SpecStatus.Draft, now);
        WriteAtomic(Path.Combine(paths.SpecDir(id), MarkdownFile), markdown);
        WriteAtomic(Path.Combine(paths.SpecDir(id), UnitsFile), JsonSerializer.Serialize(Array.Empty<Unit>(), Json.Options));
        WriteRun(run);
        return run;
    }

    public IReadOnlyList<SpecListEntry> List()
    {
        var entries = new List<SpecListEntry>();
        foreach (var dir in Directory.EnumerateDirectories(paths.SpecsDir))
        {
            var id = Path.GetFileName(dir);
            try
            {
                var run = ReadRun(dir);
                entries.Add(run is null
                    ? new SpecListEntry(null, id, $"{RunFile} not found")
                    : new SpecListEntry(run, id, null));
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                entries.Add(new SpecListEntry(null, id, $"{RunFile} unreadable: {e.Message}"));
            }
        }
        return entries
            .OrderByDescending(e => e.Run?.CreatedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(e => e.Id, StringComparer.Ordinal)
            .ToList();
    }

    public SpecRun? Get(string id) => ReadRun(DirFor(id));

    public string ReadMarkdown(string id) => File.ReadAllText(Path.Combine(DirFor(id), MarkdownFile));

    public SpecRun UpdateStatus(string id, SpecStatus status)
    {
        var run = Get(id) ?? throw new KeyNotFoundException($"Spec '{id}' not found.");
        var updated = run with { Status = status };
        WriteRun(updated, id);
        return updated;
    }

    public IReadOnlyList<Unit> ListUnits(string id)
    {
        var path = Path.Combine(DirFor(id), UnitsFile);
        if (!File.Exists(path)) return [];
        return JsonSerializer.Deserialize<List<Unit>>(File.ReadAllText(path), Json.Options) ?? [];
    }

    private string DirFor(string id)
    {
        if (!SpecIds.IsValid(id)) throw new ArgumentException($"Invalid spec id '{id}'.", nameof(id));
        return paths.SpecDir(id);
    }

    private static SpecRun? ReadRun(string dir)
    {
        var path = Path.Combine(dir, RunFile);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<SpecRun>(File.ReadAllText(path), Json.Options)
            : null;
    }

    private void WriteRun(SpecRun run, string? id = null) =>
        WriteAtomic(Path.Combine(paths.SpecDir(id ?? run.Id), RunFile), JsonSerializer.Serialize(run, Json.Options));

    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    [GeneratedRegex(@"^[\w.-]+/[\w.-]+\z")]
    private static partial Regex RepoPattern();
}

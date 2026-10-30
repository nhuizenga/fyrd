using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fyrd.Core.Specs;

public sealed partial class SpecStore(FyrdPaths paths, TimeProvider time)
{
    private readonly object _gate = new();
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
        AtomicFile.Write(Path.Combine(paths.SpecDir(id), MarkdownFile), markdown);
        AtomicFile.Write(Path.Combine(paths.SpecDir(id), UnitsFile), JsonSerializer.Serialize(Array.Empty<Unit>(), Json.Options));
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

    public SpecRun UpdateStatus(string id, SpecStatus status) => Mutate(id, r => r with { Status = status });

    /// <summary>Atomically moves the spec from one status to another; false if it was not in <paramref name="from"/>.</summary>
    public bool TryTransition(string id, SpecStatus from, SpecStatus to)
    {
        lock (_gate)
        {
            var run = Get(id) ?? throw new KeyNotFoundException($"Spec '{id}' not found.");
            if (run.Status != from) return false;
            WriteRun(run with { Status = to }, id);
            return true;
        }
    }

    public SpecRun SetLead(string id, string sessionId) =>
        Mutate(id, r => r with { SessionId = sessionId, Status = SpecStatus.Building });

    public SpecRun SetCursor(string id, string lastEventId) => Mutate(id, r => r with { LastEventId = lastEventId });

    public SpecRun Finish(string id, SpecStatus status, string? branch, string? error) =>
        Mutate(id, r => r with { Status = status, Branch = branch, Error = error });

    public void WriteUnits(string id, IReadOnlyList<Unit> units)
    {
        var dir = DirFor(id);
        lock (_gate) AtomicFile.Write(Path.Combine(dir, UnitsFile), JsonSerializer.Serialize(units, Json.Options));
    }

    public IReadOnlyList<SpecRun> ListActive() =>
        List().Select(e => e.Run).OfType<SpecRun>()
            .Where(r => r.Status is SpecStatus.Queued or SpecStatus.Building or SpecStatus.FinalReview)
            .ToList();

    private SpecRun Mutate(string id, Func<SpecRun, SpecRun> change)
    {
        lock (_gate)
        {
            var run = Get(id) ?? throw new KeyNotFoundException($"Spec '{id}' not found.");
            var updated = change(run);
            WriteRun(updated, id);
            return updated;
        }
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
        AtomicFile.Write(Path.Combine(paths.SpecDir(id ?? run.Id), RunFile), JsonSerializer.Serialize(run, Json.Options));

    [GeneratedRegex(@"^[\w.-]+/[\w.-]+\z")]
    private static partial Regex RepoPattern();
}

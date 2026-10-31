using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fyrd.Core.Specs;

public sealed partial class SpecStore(FyrdPaths paths, TimeProvider time)
{
    private readonly object _gate = new();
    private const string RunFile = "run.json";
    private const string MarkdownFile = "spec.md";
    private const string UnitsFile = "units.json";
    private const string ProposalMarkdownFile = "proposal.md";
    private const string ProposalMetaFile = "proposal.json";

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
        lock (_gate)
        {
            foreach (var dir in Directory.EnumerateDirectories(paths.SpecsDir))
            {
                var id = Path.GetFileName(dir);
                if (!SpecIds.IsValid(id))
                {
                    entries.Add(new SpecListEntry(null, id, "Not a valid spec folder name"));
                    continue;
                }
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
        }
        return entries
            .OrderByDescending(e => e.Run?.CreatedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(e => e.Id, StringComparer.Ordinal)
            .ToList();
    }

    public SpecRun? Get(string id)
    {
        var dir = DirFor(id);
        lock (_gate) return ReadRun(dir);
    }

    public string ReadMarkdown(string id)
    {
        var path = Path.Combine(DirFor(id), MarkdownFile);
        lock (_gate) return File.ReadAllText(path);
    }

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
        Mutate(id, r => r with { SessionId = sessionId, Status = SpecStatus.Building, LastEventId = null });

    public SpecRun SetResearchLead(string id, string sessionId) =>
        Mutate(id, r => r with { SessionId = sessionId, Status = SpecStatus.Researching, LastEventId = null });

    private sealed record ProposalMeta(string Summary, IReadOnlyList<string> OpenQuestions, string SessionId);

    public void SaveProposal(string id, string markdown, string summary, IReadOnlyList<string> openQuestions, string sessionId)
    {
        var dir = DirFor(id);
        lock (_gate)
        {
            var run = Get(id) ?? throw new KeyNotFoundException($"Spec '{id}' not found.");
            if (run.Status != SpecStatus.Researching)
                throw new InvalidOperationException("A proposal can only be saved while the spec is Researching.");
            AtomicFile.Write(Path.Combine(dir, ProposalMarkdownFile), markdown);
            AtomicFile.Write(Path.Combine(dir, ProposalMetaFile),
                JsonSerializer.Serialize(new ProposalMeta(summary, openQuestions, sessionId), Json.Options));
            WriteRun(run with { HasProposal = true }, id);
        }
    }

    public Proposal? GetProposal(string id)
    {
        var dir = DirFor(id);
        lock (_gate)
        {
            var md = Path.Combine(dir, ProposalMarkdownFile);
            var meta = Path.Combine(dir, ProposalMetaFile);
            if (!File.Exists(md) || !File.Exists(meta)) return null;
            var m = JsonSerializer.Deserialize<ProposalMeta>(File.ReadAllText(meta), Json.Options);
            return m is null ? null : new Proposal(File.ReadAllText(md), m.Summary, m.OpenQuestions ?? [], m.SessionId);
        }
    }

    /// <summary>Replaces spec.md with the proposal (or the owner's edit of it), keeping the old text as spec.v&lt;N&gt;.md.</summary>
    public SpecRun AcceptProposal(string id, string? editedMarkdown)
    {
        var dir = DirFor(id);
        lock (_gate)
        {
            var run = Get(id) ?? throw new KeyNotFoundException($"Spec '{id}' not found.");
            if (run.Status != SpecStatus.Draft || !run.HasProposal)
                throw new InvalidOperationException("There is no proposal to accept, or the spec is not a Draft.");
            var proposal = GetProposal(id) ?? throw new InvalidOperationException("The proposal files are missing.");
            var specPath = Path.Combine(dir, MarkdownFile);
            var n = 1;
            while (File.Exists(Path.Combine(dir, $"spec.v{n}.md"))) n++;
            AtomicFile.Write(Path.Combine(dir, $"spec.v{n}.md"), File.ReadAllText(specPath));
            AtomicFile.Write(specPath, string.IsNullOrWhiteSpace(editedMarkdown) ? proposal.Markdown : editedMarkdown);
            DeleteProposalFiles(dir);
            var updated = run with { HasProposal = false, Error = null };
            WriteRun(updated, id);
            return updated;
        }
    }

    public SpecRun DiscardProposal(string id)
    {
        var dir = DirFor(id);
        lock (_gate)
        {
            var run = Get(id) ?? throw new KeyNotFoundException($"Spec '{id}' not found.");
            DeleteProposalFiles(dir);
            var updated = run with { HasProposal = false };
            WriteRun(updated, id);
            return updated;
        }
    }

    public SpecRun UpdateMarkdown(string id, string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            throw new ArgumentException("Spec markdown must not be empty.", nameof(markdown));
        var dir = DirFor(id);
        lock (_gate)
        {
            var run = Get(id) ?? throw new KeyNotFoundException($"Spec '{id}' not found.");
            if (run.Status != SpecStatus.Draft) throw new InvalidOperationException("Only a Draft spec can be edited.");
            AtomicFile.Write(Path.Combine(dir, MarkdownFile), markdown);
            WriteRun(run, id);
            return run;
        }
    }

    private static void DeleteProposalFiles(string dir)
    {
        File.Delete(Path.Combine(dir, ProposalMarkdownFile));
        File.Delete(Path.Combine(dir, ProposalMetaFile));
    }

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
            .Where(r => r.Status is SpecStatus.Queued or SpecStatus.Researching or SpecStatus.Building or SpecStatus.FinalReview)
            .ToList();

    /// <summary>Queued or researching runs that never got a session and have not been touched for <paramref name="olderThan"/>.</summary>
    public IReadOnlyList<SpecRun> ListStaleLaunches(TimeSpan olderThan)
    {
        var cutoff = time.GetUtcNow() - olderThan;
        return List().Select(e => e.Run).OfType<SpecRun>()
            .Where(r => r.Status is SpecStatus.Queued or SpecStatus.Researching
                        && string.IsNullOrEmpty(r.SessionId)
                        && (r.UpdatedAt ?? r.CreatedAt) < cutoff)
            .ToList();
    }

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
        lock (_gate)
        {
            if (!File.Exists(path)) return [];
            return JsonSerializer.Deserialize<List<Unit>>(File.ReadAllText(path), Json.Options) ?? [];
        }
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
        AtomicFile.Write(Path.Combine(paths.SpecDir(id ?? run.Id), RunFile),
            JsonSerializer.Serialize(run with { UpdatedAt = time.GetUtcNow() }, Json.Options));

    [GeneratedRegex(@"^[\w.-]+/[\w.-]+\z")]
    private static partial Regex RepoPattern();
}

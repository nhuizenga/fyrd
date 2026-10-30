using System.Text;
using System.Text.RegularExpressions;

namespace Fyrd.Core;

public sealed partial class DecisionMemory(FyrdPaths paths)
{
    private const string GlobalFile = "global.md";
    private readonly object _gate = new();

    public void Record(string repo, MemoryScope scope, string question, string answer, DateTimeOffset at, string specId)
    {
        var file = FileFor(repo, scope);
        var entry = $"- **Q:** {OneLine(question)}\n  **A:** {OneLine(answer)}\n  _{at.UtcDateTime:yyyy-MM-dd} · spec {specId}_\n";
        lock (_gate)
        {
            var existing = File.Exists(file) ? File.ReadAllText(file) : "";
            var sep = existing.Length == 0 || existing.EndsWith('\n') ? "" : "\n";
            File.AppendAllText(file, sep + entry, new UTF8Encoding(false));
        }
    }

    public string ReadFor(string repo)
    {
        var global = ReadIfExists(Path.Combine(paths.MemoryDir, GlobalFile));
        var repoText = ReadIfExists(FileFor(repo, MemoryScope.Repo));
        return global + repoText;
    }

    private string FileFor(string repo, MemoryScope scope)
    {
        if (scope == MemoryScope.Global) return Path.Combine(paths.MemoryDir, GlobalFile);
        if (!RepoPattern().IsMatch(repo))
            throw new ArgumentException($"Invalid repo '{repo}'.", nameof(repo));
        var parts = repo.Split('/');
        if (parts.Any(p => p.Trim('.').Length == 0))
            throw new ArgumentException($"Invalid repo '{repo}'.", nameof(repo));
        return Path.Combine(paths.MemoryDir, $"{parts[0]}__{parts[1]}.md");
    }

    private static string ReadIfExists(string path) => File.Exists(path) ? File.ReadAllText(path) : "";

    private static string OneLine(string s) => LineBreaks().Replace(s.Trim(), " ");

    [GeneratedRegex(@"\s*[\r\n]+\s*")]
    private static partial Regex LineBreaks();

    [GeneratedRegex(@"^[\w.-]+/[\w.-]+\z")]
    private static partial Regex RepoPattern();
}

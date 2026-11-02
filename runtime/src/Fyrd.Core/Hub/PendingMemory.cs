using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fyrd.Core.Hub;

public sealed record PendingMemoryEntry(string Id, string Text, string Source, string SessionId, DateTimeOffset At);

/// <summary>
/// Shared-memory proposals from peasants, held until the owner approves or rejects them in the console. Kept in
/// <c>hub/pending-memory.json</c>, outside <see cref="FyrdPaths.HubMemoryDir"/>, so it never reads as a memory scope.
/// </summary>
public sealed class PendingMemory(FyrdPaths paths, HubMemory memory)
{
    public const int MaxEntries = 100;
    public const int MaxTextLength = 2000;
    public const int MaxRejected = 100;

    private readonly object _gate = new();
    private readonly string _file = Path.Combine(paths.HubDir, "pending-memory.json");
    private readonly string _rejectedFile = Path.Combine(paths.HubDir, "pending-memory-rejected.json");
    private int? _count;

    /// <summary>Newest first.</summary>
    public IReadOnlyList<PendingMemoryEntry> List()
    {
        lock (_gate) return Read().AsEnumerable().Reverse().ToList();
    }

    public int Count { get { lock (_gate) return _count ??= Read().Count; } }

    /// <summary>
    /// Queues a proposal. Returns null when it is empty, or the same text is already pending, already in shared memory, or
    /// was rejected before (the last <see cref="MaxRejected"/> rejections are remembered, so a rejected proposal stays gone).
    /// Text is clamped to <see cref="MaxTextLength"/>; past <see cref="MaxEntries"/> the oldest entries are dropped.
    /// </summary>
    public PendingMemoryEntry? Add(string text, string source, string sessionId, DateTimeOffset at)
    {
        text = Clamp(text);
        if (text.Length == 0) return null;
        lock (_gate)
        {
            var entries = Read();
            if (entries.Any(e => e.Text == text) || memory.List(HubMemory.Shared).Any(e => e.Text.Trim() == text)
                || ReadRejected().Contains(HashOf(text))) return null;
            var entry = new PendingMemoryEntry(Guid.NewGuid().ToString("N"), text, source, sessionId, at);
            entries.Add(entry);
            if (entries.Count > MaxEntries) entries.RemoveRange(0, entries.Count - MaxEntries);
            Write(entries);
            return entry;
        }
    }

    /// <summary>
    /// Moves the entry into shared memory (with edited text when given) and removes it from the queue. When shared memory
    /// already holds that text, nothing is added and the existing entry is returned.
    /// </summary>
    public MemoryEntry Approve(string id, string? editedText, DateTimeOffset now)
    {
        lock (_gate)
        {
            var entries = Read();
            var i = entries.FindIndex(e => e.Id == id);
            if (i < 0) throw new KeyNotFoundException($"No pending memory entry '{id}'.");
            var text = Clamp(editedText ?? entries[i].Text);
            var added = memory.List(HubMemory.Shared).FirstOrDefault(e => e.Text.Trim() == text)
                        ?? memory.Add(HubMemory.Shared, text, entries[i].Source, now);
            entries.RemoveAt(i);
            Write(entries);
            return added;
        }
    }

    public void Reject(string id)
    {
        lock (_gate)
        {
            var entries = Read();
            var i = entries.FindIndex(e => e.Id == id);
            if (i < 0) throw new KeyNotFoundException($"No pending memory entry '{id}'.");
            var rejected = ReadRejected();
            rejected.Add(HashOf(entries[i].Text));
            if (rejected.Count > MaxRejected) rejected.RemoveRange(0, rejected.Count - MaxRejected);
            Directory.CreateDirectory(paths.HubDir);
            AtomicFile.Write(_rejectedFile, JsonSerializer.Serialize(rejected, Json.Options));
            entries.RemoveAt(i);
            Write(entries);
        }
    }

    private static string Clamp(string? text)
    {
        text = (text ?? "").Trim();
        return text.Length > MaxTextLength ? text[..MaxTextLength].TrimEnd() : text;
    }

    private List<PendingMemoryEntry> Read()
    {
        if (!File.Exists(_file)) return [];
        try { return JsonSerializer.Deserialize<List<PendingMemoryEntry>>(File.ReadAllText(_file), Json.Options) ?? []; }
        catch (JsonException)
        {
            // Keep the unreadable file for inspection instead of letting the next write silently replace it.
            File.Move(_file, _file + ".corrupt", overwrite: true);
            return [];
        }
    }

    private List<string> ReadRejected()
    {
        if (!File.Exists(_rejectedFile)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_rejectedFile), Json.Options) ?? []; }
        catch (JsonException) { return []; }
    }

    private static string HashOf(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private void Write(List<PendingMemoryEntry> entries)
    {
        Directory.CreateDirectory(paths.HubDir);
        AtomicFile.Write(_file, JsonSerializer.Serialize(entries, Json.Options));
        _count = entries.Count;
    }
}

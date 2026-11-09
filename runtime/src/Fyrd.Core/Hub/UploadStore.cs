using Fyrd.Core.CloudSessions;

namespace Fyrd.Core.Hub;

/// <summary>
/// The Files API ids of files the owner has attached but not yet sent. In memory only (never written to disk), and an
/// id is forgotten after <see cref="Ttl"/>, matching the hour the file itself lives. Only ids Fyrd uploaded are
/// accepted back, so a request cannot make Fyrd point a session at an arbitrary file id. The number and total size
/// of waiting files is capped so nothing can pile up in the owner's Files API storage; <see cref="HasRoom"/> is a soft
/// check made before an upload (two uploads racing may overshoot it slightly).
/// </summary>
public sealed class UploadStore(TimeProvider time)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(FilesApi.ExpiresInSeconds);
    public const int MaxFiles = 10;
    public const long MaxBytes = 25L * 1024 * 1024;
    public const int MaxOutstandingFiles = 50;
    public const long MaxOutstandingBytes = 250L * 1024 * 1024;

    private readonly object _lock = new();
    private readonly Dictionary<string, (UploadedFile File, DateTimeOffset At)> _files = [];

    /// <summary>Files taken out of the store for one send. <see cref="Restore"/> puts them back if the send fails.</summary>
    public sealed class Claim
    {
        private readonly UploadStore _store;
        private readonly List<(UploadedFile File, DateTimeOffset At)> _taken;

        internal Claim(UploadStore store, List<(UploadedFile, DateTimeOffset)> taken)
        {
            _store = store;
            _taken = taken;
            Files = taken.Select(t => t.Item1).ToList();
        }

        public IReadOnlyList<UploadedFile> Files { get; }

        public void Restore()
        {
            lock (_store._lock)
                foreach (var (file, at) in _taken) _store._files[file.Id] = (file, at);
        }
    }

    /// <summary>True when another file of this size may be held without passing the outstanding caps.</summary>
    public bool HasRoom(long bytes)
    {
        lock (_lock)
        {
            Purge();
            return _files.Count < MaxOutstandingFiles && _files.Values.Sum(v => v.File.SizeBytes) + bytes <= MaxOutstandingBytes;
        }
    }

    public void Add(UploadedFile file)
    {
        lock (_lock)
        {
            Purge();
            _files[file.Id] = (file, time.GetUtcNow());
        }
    }

    public bool Forget(string id)
    {
        lock (_lock) return _files.Remove(id);
    }

    /// <summary>The files for these ids in order, or the first unknown / expired / empty id. Nothing is removed.</summary>
    public (IReadOnlyList<UploadedFile>? Files, string? Missing) Resolve(IEnumerable<string?> ids)
    {
        lock (_lock)
        {
            Purge();
            var found = new List<UploadedFile>();
            foreach (var id in ids.Distinct())
            {
                if (string.IsNullOrWhiteSpace(id) || !_files.TryGetValue(id, out var entry)) return (null, id ?? "");
                found.Add(entry.File);
            }
            return (found, null);
        }
    }

    /// <summary>
    /// Resolves and removes these ids in one step, so two sends cannot both use the same file. Null when any id is
    /// unknown (nothing is removed then).
    /// </summary>
    public Claim? TryClaim(IEnumerable<string?> ids)
    {
        lock (_lock)
        {
            Purge();
            var taken = new List<(UploadedFile, DateTimeOffset)>();
            foreach (var id in ids.Distinct())
            {
                if (string.IsNullOrWhiteSpace(id) || !_files.TryGetValue(id, out var entry)) return null;
                taken.Add((entry.File, entry.At));
            }
            foreach (var (file, _) in taken) _files.Remove(file.Id);
            return new Claim(this, taken);
        }
    }

    private void Purge()
    {
        var cutoff = time.GetUtcNow() - Ttl;
        foreach (var id in _files.Where(kv => kv.Value.At < cutoff).Select(kv => kv.Key).ToList()) _files.Remove(id);
    }
}

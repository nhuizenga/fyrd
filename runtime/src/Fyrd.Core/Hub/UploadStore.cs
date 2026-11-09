using System.Collections.Concurrent;
using Fyrd.Core.CloudSessions;

namespace Fyrd.Core.Hub;

/// <summary>
/// The Files API ids of files the owner has attached but not yet sent. In memory only (never written to disk), and an
/// id is forgotten after <see cref="Ttl"/>, matching the hour the file itself lives. Only ids Fyrd uploaded are
/// accepted back, so a request cannot make Fyrd point a session at an arbitrary file id.
/// </summary>
public sealed class UploadStore(TimeProvider time)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(FilesApi.ExpiresInSeconds);
    public const int MaxFiles = 10;
    public const long MaxBytes = 25L * 1024 * 1024;

    private readonly ConcurrentDictionary<string, (UploadedFile File, DateTimeOffset At)> _files = new();

    public void Add(UploadedFile file)
    {
        Purge();
        _files[file.Id] = (file, time.GetUtcNow());
    }

    public bool Forget(string id) => _files.TryRemove(id, out _);

    /// <summary>The files for these ids in order, or the first unknown / expired id.</summary>
    public (IReadOnlyList<UploadedFile>? Files, string? Missing) Resolve(IEnumerable<string> ids)
    {
        Purge();
        var found = new List<UploadedFile>();
        foreach (var id in ids.Distinct())
        {
            if (!_files.TryGetValue(id, out var entry)) return (null, id);
            found.Add(entry.File);
        }
        return (found, null);
    }

    /// <summary>Forgets ids once their message or answer is sent.</summary>
    public void Consume(IEnumerable<UploadedFile> files)
    {
        foreach (var f in files) _files.TryRemove(f.Id, out _);
    }

    private void Purge()
    {
        var cutoff = time.GetUtcNow() - Ttl;
        foreach (var (id, entry) in _files)
            if (entry.At < cutoff) _files.TryRemove(id, out _);
    }
}

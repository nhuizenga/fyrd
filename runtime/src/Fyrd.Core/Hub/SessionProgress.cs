using System.Collections.Concurrent;
using Fyrd.Core.CloudSessions;

namespace Fyrd.Core.Hub;

/// <summary>
/// The latest live-only "in progress" line per session, kept server-side so it survives the owner looking elsewhere:
/// a session nobody has open still has its current tool or streamed reply here. Cleared by a saved event or the
/// end-of-turn summary, like the per-stream line.
/// </summary>
public sealed class SessionProgress
{
    private readonly ConcurrentDictionary<string, TranscriptItem> _latest = new(StringComparer.Ordinal);

    /// <summary>Stores <paramref name="item"/>; an empty item (nothing in progress any more) removes the entry.</summary>
    public void Set(string sessionId, TranscriptItem item)
    {
        if (string.IsNullOrEmpty(item.Status) && string.IsNullOrEmpty(item.Text)) _latest.TryRemove(sessionId, out _);
        else _latest[sessionId] = item;
    }

    public TranscriptItem? Get(string sessionId) => _latest.TryGetValue(sessionId, out var i) ? i : null;

    public void Clear(string sessionId) => _latest.TryRemove(sessionId, out _);

    /// <summary>A copy of every session's current line, keyed by session id.</summary>
    public IReadOnlyDictionary<string, TranscriptItem> Snapshot() => new Dictionary<string, TranscriptItem>(_latest);
}

/// <summary>
/// Keeps one upstream event stream open per hub session and folds its ephemeral events into <see cref="SessionProgress"/>.
/// <see cref="Sync"/> reconciles the open streams with the sessions the last poll listed (archived ones are not watched).
/// Each stream reopens after it ends or fails, resuming after the last saved sequence it saw.
/// </summary>
public sealed class SessionProgressWatcher(ICloudSessions cloud, SessionProgress store) : IDisposable
{
    internal static readonly TimeSpan ReopenDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly Dictionary<string, (CancellationTokenSource Cts, Task Task)> _watches = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Session ids with a stream open (or reopening).</summary>
    public IReadOnlyCollection<string> Watching
    {
        get { lock (_gate) return _watches.Keys.ToArray(); }
    }

    /// <summary>Starts a stream for each newly listed session and stops those that are gone or archived.</summary>
    public void Sync(IReadOnlyList<HubSessionView> sessions)
    {
        var wanted = sessions.Where(s => s.Status != "archived").Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var id in _watches.Keys.Where(k => !wanted.Contains(k)).ToList())
            {
                _watches[id].Cts.Cancel();
                _watches.Remove(id);
                store.Clear(id);
            }
            foreach (var id in wanted.Where(w => !_watches.ContainsKey(w)))
            {
                var cts = new CancellationTokenSource();
                _watches[id] = (cts, Task.Run(() => WatchAsync(id, cts.Token)));
            }
        }
    }

    private async Task WatchAsync(string id, CancellationToken ct)
    {
        var progress = new ProgressTracker();
        long? last = null;
        var delay = ReopenDelay;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (var e in cloud.StreamEventsAsync(id, last, ct))
                {
                    if (e.Ephemeral)
                    {
                        if (progress.Apply(e) is { } live) store.Set(id, live);
                        continue;
                    }
                    if (last is { } seen && e.Sequence <= seen) continue;
                    last = e.Sequence;
                    if (progress.Reset(e.Event.CreatedAt) is { } cleared) store.Set(id, cleared);
                }
                delay = ReopenDelay;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // Signed out, a dropped connection or an API error: retry with backoff; the poll loop reports auth problems.
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxBackoff.Ticks));
            }
            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var (cts, _) in _watches.Values) cts.Cancel();
            _watches.Clear();
        }
    }
}

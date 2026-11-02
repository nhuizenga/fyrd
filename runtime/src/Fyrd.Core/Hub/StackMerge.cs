namespace Fyrd.Core.Hub;

public enum StackMergeStatus { Waiting, Merging, Done, Failed, Cancelled }

/// <summary>
/// One stack-merge job as the console sees it. <c>Prs</c> are the PR URLs from the top of the chain to the root, the order
/// they merge in; <c>Index</c> is the one being waited on or merged (all before it are merged). <c>Message</c> says why a
/// job failed or was cancelled.
/// </summary>
public sealed record StackMergeJob(
    string Id, string Repo, IReadOnlyList<string> Prs, int Index, StackMergeStatus Status, string? Message,
    DateTimeOffset StartedAt, DateTimeOffset UpdatedAt)
{
    public bool Finished => Status is StackMergeStatus.Done or StackMergeStatus.Failed or StackMergeStatus.Cancelled;
}

/// <summary>
/// Merges a whole chain of stacked PRs, top down to the root, squashing each into its base once it can merge. Between steps it
/// waits for checks (polling every <see cref="PollInterval"/>, at most <see cref="StepTimeout"/> per PR). It stops with
/// <c>Failed</c> on failing checks, a conflict, a PR closed or merged by someone else, an unexpected base, or a timeout. Cancelling
/// stops before the next merge and never undoes merges already done. One job per chain. Jobs live in memory only: a server
/// restart drops a running job (some PRs merged, the rest open), and starting again recomputes the chain from what is still open.
/// </summary>
public sealed class StackMergeJobs(
    IGitHub github, TimeProvider time, OwnedPullRequests owned, HubState state, CancellationToken stopping = default)
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(30);
    /// <summary>Finished jobs stay in hub state this long.</summary>
    public static readonly TimeSpan KeepFinished = TimeSpan.FromMinutes(10);

    private sealed class Entry(StackMergeJob job, CancellationTokenSource cts)
    {
        public StackMergeJob Job = job;
        public readonly CancellationTokenSource Cts = cts;
        public Task? Run;
    }

    private readonly Dictionary<string, Entry> _jobs = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>Why the chain cannot be merged as a stack right now, or null when it can. Checked against live statuses.</summary>
    public static string? Preconditions(IReadOnlyList<PullRequestStatus> rootToTop, bool fork, IReadOnlyCollection<string> sessionPrs)
    {
        if (rootToTop.Count < 2) return "That pull request is not stacked with another one.";
        if (fork) return "This stack forks (a pull request has more than one stacked on it), so it cannot be merged as a stack.";
        foreach (var p in rootToTop)
        {
            if (!sessionPrs.Contains(p.Url)) return $"#{p.Number} is not one of this session's pull requests.";
            if (p.State != "open") return $"#{p.Number} is not open.";
            if (p.Draft) return $"#{p.Number} is still a draft.";
            if (p.Mergeable == "conflicting") return $"#{p.Number} has merge conflicts.";
            if (p.Checks == "failing") return $"Checks are failing on #{p.Number}.";
        }
        var top = rootToTop[^1];
        return top.CanMerge ? null : $"#{top.Number} cannot be merged yet (checks must pass first).";
    }

    /// <summary>
    /// Starts a job for the chain. Returns null (with <paramref name="conflict"/>) when a job already runs on any PR of it.
    /// </summary>
    public StackMergeJob? Start(string repo, IReadOnlyList<PullRequestStatus> rootToTop, out string? conflict)
    {
        var topDown = rootToTop.Reverse().ToList();
        lock (_gate)
        {
            if (_jobs.Values.FirstOrDefault(e => !e.Job.Finished && e.Job.Prs.Any(u => topDown.Any(p => p.Url == u))) is { } busy)
            {
                conflict = $"A stack merge is already running for these pull requests (started {busy.Job.StartedAt:HH:mm} UTC).";
                return null;
            }
            var now = time.GetUtcNow();
            var job = new StackMergeJob(Guid.NewGuid().ToString("n"), repo, topDown.Select(p => p.Url).ToList(), 0,
                StackMergeStatus.Waiting, null, now, now);
            var entry = new Entry(job, CancellationTokenSource.CreateLinkedTokenSource(stopping));
            _jobs[job.Id] = entry;
            var bases = topDown.ToDictionary(p => p.Url, p => p.Base);
            entry.Run = Task.Run(() => RunAsync(entry, bases));
            conflict = null;
            return job;
        }
    }

    /// <summary>False when no such job exists. Cancelling a finished job does nothing.</summary>
    public bool Cancel(string id)
    {
        Entry? e;
        lock (_gate) _jobs.TryGetValue(id, out e);
        if (e is null) return false;
        if (!e.Job.Finished) e.Cts.Cancel();
        return true;
    }

    /// <summary>Running jobs and those finished less than <see cref="KeepFinished"/> ago. Drops the older ones.</summary>
    public IReadOnlyList<StackMergeJob> Snapshot()
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            foreach (var (id, e) in _jobs.ToList())
                if (e.Job.Finished && now - e.Job.UpdatedAt > KeepFinished) _jobs.Remove(id);
            return _jobs.Values.Select(e => e.Job).OrderBy(j => j.StartedAt).ToList();
        }
    }

    /// <summary>Test hook: completes when the job's run has ended.</summary>
    public Task? Completion(string id)
    {
        lock (_gate) return _jobs.TryGetValue(id, out var e) ? e.Run : null;
    }

    private void Update(Entry e, Func<StackMergeJob, StackMergeJob> change)
    {
        lock (_gate) e.Job = change(e.Job) with { UpdatedAt = time.GetUtcNow() };
        state.RequestPoll();
    }

    private async Task RunAsync(Entry e, IReadOnlyDictionary<string, string> expectedBase)
    {
        var ct = e.Cts.Token;
        try
        {
            var prs = e.Job.Prs;
            for (var i = 0; i < prs.Count; i++)
            {
                var index = i;
                Update(e, j => j with { Index = index, Status = StackMergeStatus.Waiting });
                var deadline = time.GetUtcNow() + StepTimeout;
                var url = prs[i];
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var st = await github.GetAsync(url, ct);
                    var n = $"#{st.Number}";
                    if (st.State == "merged") { Fail(e, $"{n} was merged by someone else."); return; }
                    if (st.State != "open") { Fail(e, $"{n} was closed."); return; }
                    if (st.Mergeable == "conflicting") { Fail(e, $"{n} has merge conflicts."); return; }
                    if (st.Checks == "failing") { Fail(e, $"Checks are failing on {n}."); return; }
                    if (st.Base != expectedBase[url]) { Fail(e, $"The base of {n} changed from {expectedBase[url]} to {st.Base}."); return; }
                    if (st.CanMerge)
                    {
                        Update(e, j => j with { Status = StackMergeStatus.Merging });
                        // Not cancellable: once a merge starts it finishes, so a cancel never leaves a half-done step.
                        await github.MergeAsync(url, CancellationToken.None);
                        var (owner, repo, _) = PullRequestUrl.Parse(url);
                        owned.Invalidate($"{owner}/{repo}");
                        state.RequestPoll();
                        break;
                    }
                    if (time.GetUtcNow() >= deadline) { Fail(e, $"{n} waited more than {(int)StepTimeout.TotalMinutes} minutes for its checks."); return; }
                    await Task.Delay(PollInterval, time, ct);
                }
            }
            Update(e, j => j with { Status = StackMergeStatus.Done, Index = j.Prs.Count });
        }
        catch (OperationCanceledException)
        {
            Update(e, j => j with { Status = StackMergeStatus.Cancelled, Message = stopping.IsCancellationRequested ? "The server stopped." : "Cancelled." });
        }
        catch (GitHubException ex)
        {
            Fail(e, ex.Message);
        }
        catch (Exception ex)
        {
            Fail(e, "Unexpected error: " + ex.Message);
        }
    }

    private void Fail(Entry e, string message) =>
        Update(e, j => j with { Status = StackMergeStatus.Failed, Message = message });
}

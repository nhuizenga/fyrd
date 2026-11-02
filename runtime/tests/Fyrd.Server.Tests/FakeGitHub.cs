using Fyrd.Core.Hub;

namespace Fyrd.Server.Tests;

public sealed class FakeGitHub : IGitHub
{
    public Dictionary<string, PullRequestStatus> Statuses { get; } = [];
    public List<string> Readied { get; } = [];
    public List<string> Merged { get; } = [];
    public Exception? Fail { get; set; }
    public bool Configured { get; set; } = true;

    public Task<string?> LoginAsync(CancellationToken ct = default) =>
        Fail is not null ? throw Fail : Task.FromResult<string?>(Configured ? "octo" : null);

    public List<PullRequestHead> Heads { get; } = [];
    public int Listings { get; private set; }

    public Task<PullRequestListing> ListAsync(string repo, CancellationToken ct = default)
    {
        if (Fail is not null) throw Fail;
        Listings++;
        return Task.FromResult(new PullRequestListing("main", Heads.ToList()));
    }

    /// <summary>
    /// Scripted statuses per URL: each <see cref="GetAsync"/> takes the next one, and the last one repeats. Used before
    /// <see cref="Statuses"/>, so a PR can go pending → passing over successive reads.
    /// </summary>
    public Dictionary<string, Queue<PullRequestStatus>> Sequences { get; } = [];

    /// <summary>Called after a merge of this URL, to update the fake world (for example mark the PR merged).</summary>
    public Action<string>? OnMerge { get; set; }

    public int Gets { get; private set; }

    public Task<PullRequestStatus> GetAsync(string url, CancellationToken ct = default)
    {
        if (Fail is not null) throw Fail;
        Gets++;
        if (Sequences.TryGetValue(url, out var q) && q.Count > 0)
        {
            var next = q.Count > 1 ? q.Dequeue() : q.Peek();
            Statuses[url] = next;
            return Task.FromResult(next);
        }
        return Task.FromResult(Statuses[url]);
    }

    public Task MarkReadyAsync(string url, CancellationToken ct = default)
    {
        if (Fail is not null) throw Fail;
        Readied.Add(url);
        return Task.CompletedTask;
    }

    public Task MergeAsync(string url, CancellationToken ct = default)
    {
        if (Fail is not null) throw Fail;
        Merged.Add(url);
        OnMerge?.Invoke(url);
        return Task.CompletedTask;
    }
}

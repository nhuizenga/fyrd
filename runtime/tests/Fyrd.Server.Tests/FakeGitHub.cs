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

    public Task<IReadOnlyList<PullRequestHead>> ListAsync(string repo, CancellationToken ct = default)
    {
        if (Fail is not null) throw Fail;
        return Task.FromResult<IReadOnlyList<PullRequestHead>>(Heads.ToList());
    }

    public Task<PullRequestStatus> GetAsync(string url, CancellationToken ct = default)
    {
        if (Fail is not null) throw Fail;
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
        return Task.CompletedTask;
    }
}

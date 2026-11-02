using Fyrd.Core.Hub;
using Microsoft.Extensions.Time.Testing;

namespace Fyrd.Server.Tests;

public sealed class StackMergeTests
{
    private const string Repo = "acme/widgets";
    private static string Url(int n) => $"https://github.com/acme/widgets/pull/{n}";

    private readonly FakeGitHub _gh = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-11-02T12:00:00Z"));
    private readonly HubState _state = new();
    private readonly StackMergeJobs _jobs;

    public StackMergeTests() => _jobs = new StackMergeJobs(_gh, _time, new OwnedPullRequests(_gh, _time), _state);

    private static PullRequestStatus St(int n, string head, string @base, string checks = "passing", string state = "open", string mergeable = "mergeable") =>
        new(Url(n), n, $"PR {n}", state, false, checks) { Head = head, Base = @base, Mergeable = mergeable };

    private static PullRequestStatus[] Stack(string checks11 = "passing", string checks12 = "passing", string checks13 = "passing") =>
        [St(11, "a", "main", checks11), St(12, "b", "a", checks12), St(13, "c", "b", checks13)];

    private StackMergeJob Start(params PullRequestStatus[] rootToTop)
    {
        foreach (var p in rootToTop) _gh.Statuses[p.Url] = p;
        var job = _jobs.Start(Repo, rootToTop, out var conflict);
        Assert.Null(conflict);
        return job!;
    }

    private StackMergeJob Get(StackMergeJob job) => _jobs.Snapshot().Single(j => j.Id == job.Id);

    /// <summary>Lets the job run, advancing the clock a poll interval at a time, until it ends (or the budget runs out).</summary>
    private async Task<StackMergeJob> Finish(StackMergeJob job, int maxSteps = 400)
    {
        var run = _jobs.Completion(job.Id)!;
        for (var i = 0; i < maxSteps && !run.IsCompleted; i++)
        {
            _time.Advance(StackMergeJobs.PollInterval);
            await Task.Delay(1);
        }
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        return Get(job);
    }

    /// <summary>Waits until the job is polling (a real tick for the Task.Run to reach its delay).</summary>
    private static Task Tick() => Task.Delay(50);

    [Fact]
    public async Task ThreePrs_MergeTopDown_ToTheRoot()
    {
        var job = await Finish(Start(Stack()));

        Assert.Equal(StackMergeStatus.Done, job.Status);
        Assert.Equal([Url(13), Url(12), Url(11)], _gh.Merged);
        Assert.Equal([Url(13), Url(12), Url(11)], job.Prs);
        Assert.Null(job.Message);
    }

    [Fact]
    public async Task WaitsThroughPendingChecks_BeforeEachMerge()
    {
        _gh.Sequences[Url(12)] = new Queue<PullRequestStatus>([St(12, "b", "a", "pending"), St(12, "b", "a", "pending"), St(12, "b", "a", "passing")]);
        var job = await Finish(Start(Stack()));

        Assert.Equal(StackMergeStatus.Done, job.Status);
        Assert.Equal([Url(13), Url(12), Url(11)], _gh.Merged);
        Assert.True(_gh.Gets >= 5, "polled #12 until it passed");
    }

    [Fact]
    public async Task EachMerge_InvalidatesTheRepoAndRequestsAPoll()
    {
        _state.ResetPollRequest();
        await Finish(Start(Stack()));
        Assert.True(_state.PollRequested.IsCompleted);
    }

    [Fact]
    public async Task StopsWhenChecksStartFailing_KeepingWhatIsAlreadyMerged()
    {
        _gh.OnMerge = u => { if (u == Url(13)) _gh.Statuses[Url(12)] = St(12, "b", "a", "failing"); };
        var job = await Finish(Start(Stack()));

        Assert.Equal(StackMergeStatus.Failed, job.Status);
        Assert.Equal("Checks are failing on #12.", job.Message);
        Assert.Equal([Url(13)], _gh.Merged);
        Assert.Equal(1, job.Index);
    }

    [Fact]
    public async Task StopsOnAConflict()
    {
        _gh.OnMerge = u => { if (u == Url(13)) _gh.Statuses[Url(12)] = St(12, "b", "a", mergeable: "conflicting"); };
        var job = await Finish(Start(Stack()));

        Assert.Equal(StackMergeStatus.Failed, job.Status);
        Assert.Equal("#12 has merge conflicts.", job.Message);
        Assert.Equal([Url(13)], _gh.Merged);
    }

    [Fact]
    public async Task StopsWhenAPrIsMergedBySomeoneElse()
    {
        _gh.OnMerge = u => { if (u == Url(13)) _gh.Statuses[Url(12)] = St(12, "b", "a", state: "merged"); };
        var job = await Finish(Start(Stack()));

        Assert.Equal(StackMergeStatus.Failed, job.Status);
        Assert.Equal("#12 was merged by someone else.", job.Message);
        Assert.Equal([Url(13)], _gh.Merged);
    }

    [Fact]
    public async Task StopsWhenAPrIsClosed()
    {
        _gh.OnMerge = u => { if (u == Url(13)) _gh.Statuses[Url(12)] = St(12, "b", "a", state: "closed"); };
        var job = await Finish(Start(Stack()));

        Assert.Equal(StackMergeStatus.Failed, job.Status);
        Assert.Equal("#12 was closed.", job.Message);
    }

    [Fact]
    public async Task StopsWhenABaseChangedUnexpectedly()
    {
        _gh.OnMerge = u => { if (u == Url(13)) _gh.Statuses[Url(12)] = St(12, "b", "main"); };
        var job = await Finish(Start(Stack()));

        Assert.Equal(StackMergeStatus.Failed, job.Status);
        Assert.Equal("The base of #12 changed from a to main.", job.Message);
        Assert.Equal([Url(13)], _gh.Merged);
    }

    [Fact]
    public async Task TimesOutAfterThirtyMinutesOfWaitingOnOnePr()
    {
        _gh.OnMerge = u => { if (u == Url(13)) _gh.Statuses[Url(12)] = St(12, "b", "a", "pending"); };
        var job = await Finish(Start(Stack()));

        Assert.Equal(StackMergeStatus.Failed, job.Status);
        Assert.Equal("#12 waited more than 30 minutes for its checks.", job.Message);
        Assert.Equal([Url(13)], _gh.Merged);
    }

    [Fact]
    public async Task EachPrGetsItsOwnThirtyMinutes()
    {
        // #13 and #12 each wait ~25 minutes: neither times out, although the whole job takes 50.
        _gh.Sequences[Url(13)] = new Queue<PullRequestStatus>(Enumerable.Repeat(St(13, "c", "b", "pending"), 75).Append(St(13, "c", "b")));
        _gh.OnMerge = u => { if (u == Url(13)) _gh.Sequences[Url(12)] = new Queue<PullRequestStatus>(Enumerable.Repeat(St(12, "b", "a", "pending"), 75).Append(St(12, "b", "a"))); };
        var job = await Finish(Start(Stack()), 800);

        Assert.Equal(StackMergeStatus.Done, job.Status);
        Assert.Equal([Url(13), Url(12), Url(11)], _gh.Merged);
    }

    [Fact]
    public async Task Cancel_StopsBeforeTheNextMerge_AndNeverUndoesWhatIsDone()
    {
        _gh.OnMerge = u => { if (u == Url(13)) _gh.Statuses[Url(12)] = St(12, "b", "a", "pending"); };
        var job = Start(Stack());
        var run = _jobs.Completion(job.Id)!;
        // Let #13 merge, then #12 sit waiting for its checks.
        for (var i = 0; i < 100 && _gh.Merged.Count == 0; i++) await Task.Delay(5);
        await Tick();
        Assert.Equal([Url(13)], _gh.Merged);
        Assert.Equal(StackMergeStatus.Waiting, Get(job).Status);

        Assert.True(_jobs.Cancel(job.Id));
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        var after = Get(job);
        Assert.Equal(StackMergeStatus.Cancelled, after.Status);
        Assert.Equal("Cancelled.", after.Message);
        Assert.Equal([Url(13)], _gh.Merged);
    }

    [Fact]
    public void Cancel_UnknownJob_IsFalse() => Assert.False(_jobs.Cancel("nope"));

    [Fact]
    public async Task ASecondJobOnTheSameChain_IsRejected_UntilTheFirstEnds()
    {
        _gh.Sequences[Url(13)] = new Queue<PullRequestStatus>([St(13, "c", "b", "pending")]);
        var first = Start(Stack());
        await Tick();

        // Any PR in the running chain, even a different chain slice, is refused.
        Assert.Null(_jobs.Start(Repo, [St(12, "b", "a"), St(13, "c", "b")], out var conflict));
        Assert.Contains("already running", conflict);

        _jobs.Cancel(first.Id);
        await _jobs.Completion(first.Id)!.WaitAsync(TimeSpan.FromSeconds(10));
        _gh.Sequences.Clear();
        Assert.NotNull(_jobs.Start(Repo, [St(12, "b", "a"), St(13, "c", "b")], out _));
    }

    [Fact]
    public async Task ADifferentChain_CanRunAtTheSameTime()
    {
        _gh.Sequences[Url(13)] = new Queue<PullRequestStatus>([St(13, "c", "b", "pending")]);
        var first = Start(Stack());
        await Tick();
        var other = new[] { St(31, "x", "main"), St(32, "y", "x") };
        Assert.NotNull(_jobs.Start(Repo, other, out _));
        _jobs.Cancel(first.Id);
    }

    [Fact]
    public async Task FinishedJobs_StayInStateTenMinutes_ThenAreDropped()
    {
        var job = await Finish(Start(Stack()));
        Assert.Single(_jobs.Snapshot());
        _time.Advance(StackMergeJobs.KeepFinished - TimeSpan.FromSeconds(1));
        Assert.Single(_jobs.Snapshot());
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Empty(_jobs.Snapshot());
        Assert.Equal(StackMergeStatus.Done, job.Status);
    }

    [Fact]
    public async Task AGitHubError_FailsTheJobWithItsMessage()
    {
        var stack = Stack();
        foreach (var p in stack) _gh.Statuses[p.Url] = p;
        _gh.Fail = new GitHubException("GitHub refused the merge: Merge method not allowed");
        var job = _jobs.Start(Repo, stack, out _)!;
        var after = await Finish(job);

        Assert.Equal(StackMergeStatus.Failed, after.Status);
        Assert.Contains("Merge method not allowed", after.Message);
    }

    // --- Preconditions ---

    private static string? Check(PullRequestStatus[] rootToTop, bool fork = false, string[]? session = null) =>
        StackMergeJobs.Preconditions(rootToTop, fork, session ?? rootToTop.Select(p => p.Url).ToArray());

    [Fact]
    public void Preconditions_AcceptAHealthyChain_PendingBelowTheTopIsAllowed() =>
        Assert.Null(Check(Stack(checks11: "pending", checks12: "pending")));

    [Fact]
    public void Preconditions_RejectASinglePr() =>
        Assert.Contains("not stacked", Check([St(1, "a", "main")]));

    [Fact]
    public void Preconditions_RejectAFork() =>
        Assert.Contains("forks", Check(Stack(), fork: true));

    [Fact]
    public void Preconditions_RejectADraft()
    {
        var s = Stack();
        s[1] = s[1] with { Draft = true };
        Assert.Equal("#12 is still a draft.", Check(s));
    }

    [Fact]
    public void Preconditions_RejectAConflictAndFailingChecksAndANonOpenPr()
    {
        Assert.Equal("#12 has merge conflicts.", Check([St(11, "a", "main"), St(12, "b", "a", mergeable: "conflicting"), St(13, "c", "b")]));
        Assert.Equal("Checks are failing on #11.", Check(Stack(checks11: "failing")));
        Assert.Equal("#12 is not open.", Check([St(11, "a", "main"), St(12, "b", "a", state: "merged"), St(13, "c", "b")]));
    }

    [Fact]
    public void Preconditions_TheTopPrMustBeAbleToMergeNow() =>
        Assert.Equal("#13 cannot be merged yet (checks must pass first).", Check(Stack(checks13: "pending")));

    [Fact]
    public void Preconditions_EveryPrMustBeInTheSessionsList() =>
        Assert.Equal("#12 is not one of this session's pull requests.", Check(Stack(), session: [Url(11), Url(13)]));
}

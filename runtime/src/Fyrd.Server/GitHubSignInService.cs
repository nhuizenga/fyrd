using Fyrd.Core.Hub;

namespace Fyrd.Server;

/// <summary>Checks the GitHub token at startup and every 10 minutes.</summary>
public sealed class GitHubSignInService(GitHubSignIn signIn) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(GitHubSignIn.Interval);
        do
        {
            try { await signIn.RefreshAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}

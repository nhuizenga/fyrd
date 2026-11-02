using Fyrd.Core.Hub;

namespace Fyrd.Server;

/// <summary>Keeps <see cref="SessionProgressWatcher"/> in step with the sessions the last hub poll listed.</summary>
public sealed class SessionProgressService(SessionProgressWatcher watcher, HubState state) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            do watcher.Sync(state.Sessions);
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { }
        finally { watcher.Dispose(); }
    }
}

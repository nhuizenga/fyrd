using Fyrd.Core.Hub;

namespace Fyrd.Server;

/// <summary>Runs <see cref="HubRunner"/> passes sequentially: every 10 s, or every 60 s while signed out.</summary>
public sealed class HubRunnerService(HubRunner runner, HubState state, ILogger<HubRunnerService> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await runner.PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                state.LastError = e.Message;
                log.LogError(e, "Hub poll pass failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(state.SignedOut ? 60 : 10), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}

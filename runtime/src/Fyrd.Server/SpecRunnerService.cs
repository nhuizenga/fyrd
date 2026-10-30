using Fyrd.Core.Orchestration;

namespace Fyrd.Server;

/// <summary>Runs <see cref="SpecRunner"/> passes sequentially: every 10 s, or every 60 s while signed out.</summary>
public sealed class SpecRunnerService(SpecRunner runner, RunnerState state, ILogger<SpecRunnerService> log)
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
                log.LogError(e, "Spec poll pass failed");
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

using Fyrd.Core.Config;
using Fyrd.Core.Orchestration;

namespace Fyrd.Server.Endpoints;

internal static class ConfigEndpoints
{
    public static IEndpointRouteBuilder MapConfig(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/config");

        group.MapGet("/", (ConfigStore config) => Results.Ok(config.Load()));

        group.MapPut("/", (FyrdConfig? body, ConfigStore config, LeadRelay relay, CancellationToken ct) => ApiErrors.RunAsync(async () =>
        {
            if (body is null) return Results.Problem("Body is required.", statusCode: StatusCodes.Status400BadRequest);
            var previousCap = config.Load().SquadCap;
            config.Save(body);
            var saved = config.Load();
            if (saved.SquadCap != previousCap)
            {
                try
                {
                    await relay.RelayConfigAsync(saved.SquadCap, ct);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    return ApiErrors.OkWithRelayError(saved, e.Message);
                }
            }
            return Results.Ok(saved);
        }));

        return app;
    }
}

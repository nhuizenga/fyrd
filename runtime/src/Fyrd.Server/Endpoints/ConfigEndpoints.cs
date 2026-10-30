using Fyrd.Core.Config;

namespace Fyrd.Server.Endpoints;

internal static class ConfigEndpoints
{
    public static IEndpointRouteBuilder MapConfig(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/config");

        group.MapGet("/", (ConfigStore config) => Results.Ok(config.Load()));

        group.MapPut("/", (FyrdConfig? body, ConfigStore config) => ApiErrors.Run(() =>
        {
            if (body is null) return Results.Problem("Body is required.", statusCode: StatusCodes.Status400BadRequest);
            config.Save(body);
            return Results.Ok(config.Load());
        }));

        return app;
    }
}

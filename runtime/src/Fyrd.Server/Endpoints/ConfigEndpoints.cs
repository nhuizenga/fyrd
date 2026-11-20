using System.Text.Json;
using Fyrd.Core.Config;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace Fyrd.Server.Endpoints;

internal static class ConfigEndpoints
{
    public static IEndpointRouteBuilder MapConfig(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/config");

        group.MapGet("/", (ConfigStore config) => Results.Ok(config.Load()));

        group.MapPut("/", (HttpRequest request, IOptions<JsonOptions> json, ConfigStore config, CancellationToken ct) => ApiErrors.RunAsync(async () =>
        {
            JsonElement raw;
            try { raw = await request.ReadFromJsonAsync<JsonElement>(json.Value.SerializerOptions, ct); }
            catch (JsonException) { return Results.Problem("Body is required.", statusCode: StatusCodes.Status400BadRequest); }
            if (raw.ValueKind != JsonValueKind.Object || raw.Deserialize<FyrdConfig>(json.Value.SerializerOptions) is not { } body)
                return Results.Problem("Body is required.", statusCode: StatusCodes.Status400BadRequest);
            // A client that predates the spend-limit fields must not reset them: fields it omits keep their stored value.
            bool Has(string name) => raw.EnumerateObject().Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            return Results.Ok(config.Update(stored => body with
            {
                SessionDailyLimitUsd = Has(nameof(FyrdConfig.SessionDailyLimitUsd)) ? body.SessionDailyLimitUsd : stored.SessionDailyLimitUsd,
                FyrdDailyLimitUsd = Has(nameof(FyrdConfig.FyrdDailyLimitUsd)) ? body.FyrdDailyLimitUsd : stored.FyrdDailyLimitUsd,
                TimeZone = Has(nameof(FyrdConfig.TimeZone)) ? body.TimeZone : stored.TimeZone,
                // Only the session-skill endpoints record a confirmed plugin setup script. It was confirmed for one environment,
                // so a change of environment drops it: sessions there get the skills' text until the script is pasted again.
                SessionSkillsPlugin = string.Equals(body.EnvironmentId, stored.EnvironmentId, StringComparison.Ordinal) && stored.SessionSkillsPlugin,
            }));
        }));

        return app;
    }
}

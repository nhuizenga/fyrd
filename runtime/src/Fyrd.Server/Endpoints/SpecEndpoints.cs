using System.Text.Json;
using Fyrd.Core.CloudSessions;
using Fyrd.Core.Config;
using Fyrd.Core.Orchestration;
using Fyrd.Core.Specs;

namespace Fyrd.Server.Endpoints;

internal sealed record CreateSpecRequest(string? Title, string? Repo, string? Markdown);

internal static class SpecEndpoints
{
    public static IEndpointRouteBuilder MapSpecs(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/specs");

        group.MapGet("/", (SpecStore specs) => Results.Ok(specs.List()));

        group.MapPost("/", (CreateSpecRequest? body, SpecStore specs) => ApiErrors.Run(() =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Title))
                return Results.Problem("Title is required.", statusCode: StatusCodes.Status400BadRequest);
            var run = specs.Create(body.Title, body.Repo ?? "", body.Markdown ?? "");
            return Results.Created($"/api/specs/{run.Id}", run);
        }));

        group.MapGet("/{id}", (string id, SpecStore specs) => ApiErrors.Run(() =>
        {
            SpecRun? run;
            try { run = specs.Get(id); }
            catch (JsonException e) { return Malformed(id, "run.json", e); }
            if (run is null) return Results.Problem($"Spec '{id}' not found.", statusCode: StatusCodes.Status404NotFound);
            try
            {
                return Results.Ok(new { run, markdown = specs.ReadMarkdown(id), units = specs.ListUnits(id) });
            }
            catch (JsonException e) { return Malformed(id, "units.json", e); }
        }));

        group.MapPost("/{id}/attack", (string id, SpecStore specs, ConfigStore config, PersonasSource personas,
            IServiceProvider sp, CancellationToken ct) => ApiErrors.RunAsync(async () =>
        {
            var run = specs.Get(id);
            if (run is null) return Results.Problem($"Spec '{id}' not found.", statusCode: StatusCodes.Status404NotFound);
            if (config.Load().EnvironmentId is null)
                return Results.Problem("Set the cloud environment in Settings", statusCode: StatusCodes.Status409Conflict);
            if (personas.Value is null)
                return Results.Problem($"Personas not found at {personas.Dir}", statusCode: StatusCodes.Status409Conflict);
            var launcher = sp.GetRequiredService<SpecLauncher>();
            if (!specs.TryTransition(id, SpecStatus.Draft, SpecStatus.Queued))
                return Results.Problem($"Spec '{id}' is not Draft.", statusCode: StatusCodes.Status409Conflict);
            try
            {
                return Results.Ok(await launcher.LaunchAsync(id, ct));
            }
            catch (Exception e) when (e is ClaudeAuthMissingException or CloudSessionAuthException)
            {
                return Results.Problem("Signed out of Claude — run `claude auth login`", statusCode: StatusCodes.Status502BadGateway);
            }
            catch (CloudSessionApiException e)
            {
                return Results.Problem(e.Message, statusCode: StatusCodes.Status502BadGateway);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                return Results.Problem("Timed out creating the lead session", statusCode: StatusCodes.Status502BadGateway);
            }
        }));

        return app;
    }

    private static IResult Malformed(string id, string file, JsonException e) =>
        Results.Problem($"Spec '{id}' has a malformed {file}: {e.Message}",
            statusCode: StatusCodes.Status422UnprocessableEntity);
}

using System.Text.Json;
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

        group.MapPost("/{id}/attack", (string id, SpecStore specs) => ApiErrors.Run(() =>
        {
            var run = specs.Get(id);
            if (run is null) return Results.Problem($"Spec '{id}' not found.", statusCode: StatusCodes.Status404NotFound);
            if (run.Status != SpecStatus.Draft)
                return Results.Problem($"Spec '{id}' is {run.Status}, not Draft.", statusCode: StatusCodes.Status409Conflict);
            // Plan 2 starts the Claude Code session here.
            return Results.Ok(specs.UpdateStatus(id, SpecStatus.Queued));
        }));

        return app;
    }

    private static IResult Malformed(string id, string file, JsonException e) =>
        Results.Problem($"Spec '{id}' has a malformed {file}: {e.Message}",
            statusCode: StatusCodes.Status422UnprocessableEntity);
}

using System.Text;
using Fyrd.Core;

namespace Fyrd.Server.Endpoints;

internal static class MemoryEndpoints
{
    private const int MaxBytes = 200 * 1024;

    private sealed record MemoryBody(string? Text);

    public static IEndpointRouteBuilder MapMemory(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/memory");

        group.MapGet("/", (OwnerProxy owner, DecisionMemory memory) => ApiErrors.Run(() => Results.Ok(new
        {
            ownerProxy = owner.Read(),
            global = memory.ReadFile(MemoryScope.Global, null),
            repos = memory.ListRepos().Select(r => new { repo = r, text = memory.ReadFile(MemoryScope.Repo, r) }).ToList(),
        })));

        group.MapPut("/owner-proxy", (MemoryBody? body, OwnerProxy owner) =>
            Save(body, text => owner.Save(text)));

        group.MapPut("/global", (MemoryBody? body, DecisionMemory memory) =>
            Save(body, text => memory.WriteFile(MemoryScope.Global, null, text)));

        group.MapPut("/repo", (string? repo, MemoryBody? body, DecisionMemory memory) =>
            Save(body, text => memory.WriteFile(MemoryScope.Repo, repo, text)));

        return app;
    }

    private static IResult Save(MemoryBody? body, Action<string> write) => ApiErrors.Run(() =>
    {
        if (body?.Text is null) return Results.Problem("Body { text } is required.", statusCode: StatusCodes.Status400BadRequest);
        if (Encoding.UTF8.GetByteCount(body.Text) > MaxBytes)
            return Results.Problem("Text is larger than 200 KB.", statusCode: StatusCodes.Status400BadRequest);
        write(body.Text);
        return Results.NoContent();
    });
}

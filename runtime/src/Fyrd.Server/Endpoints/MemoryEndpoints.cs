using System.Security.Cryptography;
using System.Text;
using Fyrd.Core;

namespace Fyrd.Server.Endpoints;

internal static class MemoryEndpoints
{
    private const int MaxBytes = 200 * 1024;

    private sealed record MemoryBody(string? Text, string? BaseVersion);

    public static IEndpointRouteBuilder MapMemory(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/memory");

        group.MapGet("/", (OwnerProxy owner, DecisionMemory memory) => ApiErrors.Run(() => Results.Ok(new
        {
            ownerProxy = owner.Read(),
            ownerProxyVersion = VersionOf(owner.Read()),
            global = memory.ReadFile(MemoryScope.Global, null),
            globalVersion = VersionOf(memory.ReadFile(MemoryScope.Global, null)),
            repos = memory.ListRepos().Select(r =>
            {
                var text = memory.ReadFile(MemoryScope.Repo, r);
                return new { repo = r, text, version = VersionOf(text) };
            }).ToList(),
        })));

        group.MapPut("/owner-proxy", (MemoryBody? body, OwnerProxy owner) =>
            Save(body, owner.Read, text => owner.Save(text)));

        group.MapPut("/global", (MemoryBody? body, DecisionMemory memory) =>
            Save(body, () => memory.ReadFile(MemoryScope.Global, null), text => memory.WriteFile(MemoryScope.Global, null, text)));

        group.MapPut("/repo", (string? repo, MemoryBody? body, DecisionMemory memory) =>
            Save(body, () => memory.ReadFile(MemoryScope.Repo, repo), text => memory.WriteFile(MemoryScope.Repo, repo, text)));

        return app;
    }

    private static string VersionOf(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static IResult Save(MemoryBody? body, Func<string> read, Action<string> write) => ApiErrors.Run(() =>
    {
        if (body?.Text is null) return Results.Problem("Body { text } is required.", statusCode: StatusCodes.Status400BadRequest);
        if (Encoding.UTF8.GetByteCount(body.Text) > MaxBytes)
            return Results.Problem("Text is larger than 200 KB.", statusCode: StatusCodes.Status400BadRequest);
        if (body.BaseVersion is not null && body.BaseVersion != VersionOf(read()))
            return Results.Problem("This file changed since you loaded it (new decisions were remembered). Reload and reapply your edit.",
                statusCode: StatusCodes.Status409Conflict);
        write(body.Text);
        return Results.NoContent();
    });
}

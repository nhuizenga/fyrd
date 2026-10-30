using Fyrd.Core;

namespace Fyrd.Server.Endpoints;

internal sealed record AnswerRequest(string? Text, bool? Remember, MemoryScope? Scope);

internal static class InboxEndpoints
{
    public static IEndpointRouteBuilder MapInbox(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/inbox");

        group.MapGet("/", (InboxStore inbox) => Results.Ok(inbox.ListOpen()));

        group.MapPost("/{id}/answer", (string id, AnswerRequest? body, InboxStore inbox) => ApiErrors.Run(() =>
            Results.Ok(inbox.Answer(id, body?.Text ?? "", body?.Remember ?? true, body?.Scope ?? MemoryScope.Repo))));

        return app;
    }
}

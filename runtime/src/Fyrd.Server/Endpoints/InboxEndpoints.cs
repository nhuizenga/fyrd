using Fyrd.Core;
using Fyrd.Core.Orchestration;

namespace Fyrd.Server.Endpoints;

internal sealed record AnswerRequest(string? Text, bool? Remember, MemoryScope? Scope);

internal static class InboxEndpoints
{
    public static IEndpointRouteBuilder MapInbox(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/inbox");

        group.MapGet("/", (InboxStore inbox) => Results.Ok(inbox.ListOpen()));

        group.MapPost("/{id}/answer", (string id, AnswerRequest? body, InboxStore inbox, LeadRelay relay, CancellationToken ct) => ApiErrors.RunAsync(async () =>
        {
            var card = inbox.Answer(id, body?.Text ?? "", body?.Remember ?? true, body?.Scope ?? MemoryScope.Repo);
            try
            {
                await relay.RelayAnswerAsync(card, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return ApiErrors.OkWithRelayError(card, e.Message);
            }
            return Results.Ok(card);
        }));

        return app;
    }
}

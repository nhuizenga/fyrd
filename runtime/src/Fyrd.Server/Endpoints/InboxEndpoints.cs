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

        group.MapGet("/undelivered", (InboxStore inbox) => Results.Ok(inbox.ListUndelivered()));

        group.MapPost("/{id}/answer", (string id, AnswerRequest? body, InboxStore inbox, LeadRelay relay, CancellationToken ct) => ApiErrors.RunAsync(async () =>
        {
            var card = inbox.Answer(id, body?.Text ?? "", body?.Remember ?? true, body?.Scope ?? MemoryScope.Repo);
            return await RelayOnce(inbox, relay, card, ct);
        }));

        group.MapPost("/{id}/relay", (string id, InboxStore inbox, LeadRelay relay, CancellationToken ct) => ApiErrors.RunAsync(async () =>
            await RelayOnce(inbox, relay, inbox.ResetRelay(id), ct)));

        return app;
    }

    private static async Task<IResult> RelayOnce(InboxStore inbox, LeadRelay relay, InboxCard pending, CancellationToken ct)
    {
        var error = await relay.DeliverAsync(pending, ct);
        var card = inbox.Get(pending.Id);
        return error is null ? Results.Ok(card) : ApiErrors.OkWithRelayError(card, error);
    }
}

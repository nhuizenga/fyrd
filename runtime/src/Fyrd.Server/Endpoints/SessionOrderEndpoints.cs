using Fyrd.Core.Config;

namespace Fyrd.Server.Endpoints;

internal static class SessionOrderEndpoints
{
    internal sealed record OrderBody(List<string>? Sessions, List<string>? Groups);

    public static IEndpointRouteBuilder MapSessionOrder(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/session-order");
        group.MapGet("/", (SessionOrderStore store) => Results.Ok(store.Load()));
        group.MapPut("/", (OrderBody? body, SessionOrderStore store) =>
        {
            if (body is null) return Results.Problem("Body is required.", statusCode: StatusCodes.Status400BadRequest);
            var order = SessionOrder.Normalize(body.Sessions, body.Groups);
            if (order is null) return Results.Problem($"At most {SessionOrder.MaxEntries} entries per list.", statusCode: StatusCodes.Status400BadRequest);
            store.Save(order);
            return Results.Ok(order);
        });
        return app;
    }
}

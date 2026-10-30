using System.Text.Json;
using Fyrd.Core;

namespace Fyrd.Server.Endpoints;

/// <summary>Maps store exceptions to ProblemDetails results.</summary>
internal static class ApiErrors
{
    /// <summary>The value as JSON with an extra relayError message (message only, never a body or token).</summary>
    public static IResult OkWithRelayError<T>(T value, string relayError)
    {
        var node = (System.Text.Json.Nodes.JsonObject)System.Text.Json.JsonSerializer.SerializeToNode(value, Fyrd.Core.Json.Options)!;
        node["relayError"] = relayError;
        return Results.Ok(node);
    }

    public static async Task<IResult> RunAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception e) when (e is CardAlreadyAnsweredException or KeyNotFoundException or System.Text.Json.JsonException or ArgumentException)
        {
            return Run(() => throw e);
        }
    }

    public static IResult Run(Func<IResult> action)
    {
        try
        {
            return action();
        }
        catch (CardAlreadyAnsweredException e)
        {
            return Results.Problem(e.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (KeyNotFoundException e)
        {
            return Results.Problem(e.Message, statusCode: StatusCodes.Status404NotFound);
        }
        catch (JsonException e)
        {
            return Results.Problem($"The spec's files are malformed: {e.Message}",
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }
        catch (ArgumentException e)
        {
            return Results.Problem(e.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }
}

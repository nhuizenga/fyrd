using System.Text.Json;

namespace Fyrd.Server.Endpoints;

/// <summary>Maps store exceptions to ProblemDetails results.</summary>
internal static class ApiErrors
{
    public static async Task<IResult> RunAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception e) when (e is KeyNotFoundException or JsonException or ArgumentException)
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
        catch (KeyNotFoundException e)
        {
            return Results.Problem(e.Message, statusCode: StatusCodes.Status404NotFound);
        }
        catch (JsonException e)
        {
            return Results.Problem($"Malformed JSON: {e.Message}",
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }
        catch (ArgumentException e)
        {
            return Results.Problem(e.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }
}

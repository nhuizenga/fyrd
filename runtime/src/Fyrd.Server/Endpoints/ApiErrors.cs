using Fyrd.Core;

namespace Fyrd.Server.Endpoints;

/// <summary>Maps store exceptions to ProblemDetails results.</summary>
internal static class ApiErrors
{
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
        catch (ArgumentException e)
        {
            return Results.Problem(e.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }
}

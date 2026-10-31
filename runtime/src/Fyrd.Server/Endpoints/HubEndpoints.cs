using Fyrd.Core.CloudSessions;
using Fyrd.Core.Hub;

namespace Fyrd.Server.Endpoints;

/// <summary>The console's hub API: state, Controller, hub-session actions, question answers, personas and hub memory.</summary>
internal static class HubEndpoints
{
    private const int MaxText = 20_000;
    private const int MessageCount = 50;
    private const string ResumeText = "Continue where you left off.";
    private const string NotPending = "This question is no longer waiting for an answer.";

    private sealed record TextBody(string? Text);
    private sealed record AnswerBody(string? Choice, string? Text);
    private sealed record MessageView(string Role, string Text, DateTimeOffset At);

    public static IEndpointRouteBuilder MapHub(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/hub");

        group.MapGet("/state", (HubState state, ControllerManager controller) => ApiErrors.Run(() =>
        {
            var id = controller.CurrentId;
            return Results.Ok(new
            {
                sessions = state.Sessions,
                questions = state.Questions,
                signedOut = state.SignedOut,
                lastError = state.LastError,
                lastPollAt = state.LastPollAt,
                controller = id is null ? null : new { sessionId = id, url = "https://claude.ai/code/" + id },
            });
        }));

        group.MapPost("/controller", (ControllerManager controller, CancellationToken ct) =>
            Cloud(async () => Results.Ok(await controller.FindOrCreateAsync(ct))));

        group.MapPost("/controller/recreate", (ControllerManager controller, CancellationToken ct) =>
            Cloud(async () => Results.Ok(await controller.RecreateAsync(ct))));

        group.MapGet("/sessions/{id}/messages", (string id, HubState state) =>
            InHub(id, state, () => Task.FromResult(Results.Ok(RecentMessages(state, id)))));

        group.MapPost("/sessions/{id}/message", (string id, TextBody? body, HubState state, ICloudSessions cloud, CancellationToken ct) =>
            InHub(id, state, () =>
            {
                if (TextError(body?.Text) is { } error) return Task.FromResult(error);
                return Cloud(async () =>
                {
                    await cloud.SendMessageAsync(id, body!.Text!, ct);
                    return Results.NoContent();
                });
            }));

        group.MapPost("/sessions/{id}/pause", (string id, HubState state, ICloudSessions cloud, CancellationToken ct) =>
            InHub(id, state, () => Cloud(async () =>
            {
                await cloud.InterruptAsync(id, ct);
                return Results.NoContent();
            })));

        group.MapPost("/sessions/{id}/resume", (string id, HubState state, ICloudSessions cloud, CancellationToken ct) =>
            InHub(id, state, () => Cloud(async () =>
            {
                await cloud.SendMessageAsync(id, ResumeText, ct);
                return Results.NoContent();
            })));

        group.MapPost("/questions/{id}/answer", (string id, AnswerBody? body, HubState state, ICloudSessions cloud, CancellationToken ct) =>
        {
            var q = state.Questions.FirstOrDefault(x => x.Id == id);
            if (q is null) return Task.FromResult(Results.Problem(NotPending, statusCode: StatusCodes.Status409Conflict));

            var hasChoice = !string.IsNullOrEmpty(body?.Choice);
            var hasText = !string.IsNullOrWhiteSpace(body?.Text);
            if (hasChoice == hasText)
                return Task.FromResult(Bad("Send exactly one of choice or text."));
            if (hasChoice && !q.Options.Contains(body!.Choice!))
                return Task.FromResult(Bad($"'{body.Choice}' is not one of this question's options."));
            if (hasText && body!.Text!.Length > MaxText)
                return Task.FromResult(Bad($"Text is longer than {MaxText} characters."));

            // One can_use_tool request can carry several questions; it gets exactly one control_response with all answers.
            var progress = state.RecordAnswer(q.Id, hasChoice ? body!.Choice! : body!.Text!);
            if (!progress.Pending) return Task.FromResult(Results.Problem(NotPending, statusCode: StatusCodes.Status409Conflict));
            if (!progress.Ready)
                return Task.FromResult(Results.Json(new { waitingFor = progress.WaitingFor }, statusCode: StatusCodes.Status202Accepted));
            if (!state.TryClaim(q.RequestId))
                return Task.FromResult(Results.Problem(NotPending, statusCode: StatusCodes.Status409Conflict));
            return Cloud(async () =>
            {
                var sent = false;
                try
                {
                    await cloud.AnswerQuestionAsync(q.SessionId, q, progress.Answers!, ct);
                    sent = true;
                }
                finally
                {
                    if (!sent) state.Release(q.RequestId);
                }
                state.CompleteRequest(q.RequestId);
                return Results.NoContent();
            });
        });

        group.MapGet("/personas", (HubPersonas personas) => ApiErrors.Run(() => Results.Ok(personas.List())));

        group.MapGet("/personas/{id}", (string id, HubPersonas personas) => ApiErrors.Run(() =>
            personas.Get(id) is { } p
                ? Results.Ok(p)
                : Results.Problem($"Persona '{id}' not found.", statusCode: StatusCodes.Status404NotFound)));

        group.MapPut("/personas", (HubPersona? body, HubPersonas personas) => SavePersona(null, body, personas));
        group.MapPut("/personas/{id}", (string id, HubPersona? body, HubPersonas personas) => SavePersona(id, body, personas));

        group.MapDelete("/personas/{id}", (string id, HubPersonas personas) => ApiErrors.Run(() =>
        {
            try
            {
                return personas.Delete(id)
                    ? Results.NoContent()
                    : Results.Problem($"Persona '{id}' not found.", statusCode: StatusCodes.Status404NotFound);
            }
            catch (InvalidOperationException e) { return Results.Problem(e.Message, statusCode: StatusCodes.Status409Conflict); }
        }));

        group.MapGet("/memory", (HubMemory memory, HubPersonas personas) => ApiErrors.Run(() =>
        {
            var all = memory.All();
            IReadOnlyList<MemoryEntry> Entries(string scope) => all.TryGetValue(scope, out var e) ? e : [];
            var list = personas.List();
            var scopes = new List<object> { new { scope = HubMemory.Shared, label = "Shared", entries = Entries(HubMemory.Shared) } };
            scopes.AddRange(list.Select(p => new { scope = p.Id, label = string.IsNullOrWhiteSpace(p.Name) ? p.Id : p.Name, entries = Entries(p.Id) }));
            // Scopes left behind by deleted personas stay visible so their entries can still be edited or removed.
            var known = list.Select(p => p.Id).Append(HubMemory.Shared).ToHashSet(StringComparer.Ordinal);
            scopes.AddRange(all.Keys.Where(k => !known.Contains(k)).Order(StringComparer.Ordinal)
                .Select(k => new { scope = k, label = k, entries = all[k] }));
            return Results.Ok(new { scopes });
        }));

        group.MapPost("/memory/{scope}", (string scope, TextBody? body, HubMemory memory, HubPersonas personas, TimeProvider time) =>
            ApiErrors.Run(() =>
            {
                if (TextError(body?.Text) is { } error) return error;
                if (scope != HubMemory.Shared && personas.Get(scope) is null)
                    throw new ArgumentException($"Unknown memory scope '{scope}'.", nameof(scope));
                return Results.Ok(memory.Add(scope, body!.Text!, "owner", time.GetUtcNow()));
            }));

        group.MapPut("/memory/{scope}/{entryId}", (string scope, string entryId, TextBody? body, HubMemory memory) =>
            ApiErrors.Run(() =>
            {
                if (TextError(body?.Text) is { } error) return error;
                memory.Update(scope, entryId, body!.Text!);
                return Results.NoContent();
            }));

        group.MapDelete("/memory/{scope}/{entryId}", (string scope, string entryId, HubMemory memory) =>
            ApiErrors.Run(() =>
            {
                memory.Delete(scope, entryId);
                return Results.NoContent();
            }));

        return app;
    }

    private static IResult SavePersona(string? id, HubPersona? body, HubPersonas personas) => ApiErrors.Run(() =>
    {
        if (body is null) return Bad("Body is required.");
        // Locked is set only by seeding; the console cannot lock or unlock a persona.
        var p = body with { Id = id ?? body.Id ?? "", Locked = false };
        try { return Results.Ok(personas.Save(p)); }
        catch (InvalidOperationException e) { return Results.Problem(e.Message, statusCode: StatusCodes.Status409Conflict); }
    });

    /// <summary>The last user/assistant messages from the poll's in-memory event window; no cloud calls.</summary>
    private static IReadOnlyList<MessageView> RecentMessages(HubState state, string id) =>
        state.RecentEvents(id)
            .Where(e => e.Type is "user" or "assistant" && !string.IsNullOrWhiteSpace(e.Text))
            .TakeLast(MessageCount)
            .Select(e => new MessageView(e.Type == "user" ? "user" : "agent", e.Text!, e.CreatedAt))
            .ToList();

    private static IResult? TextError(string? text) =>
        string.IsNullOrWhiteSpace(text) ? Bad("Body { text } is required.")
        : text.Length > MaxText ? Bad($"Text is longer than {MaxText} characters.")
        : null;

    private static IResult Bad(string detail) => Results.Problem(detail, statusCode: StatusCodes.Status400BadRequest);

    /// <summary>Only sessions the last hub poll saw may be driven from the console.</summary>
    private static Task<IResult> InHub(string id, HubState state, Func<Task<IResult>> action) =>
        state.Sessions.Any(s => s.Id == id)
            ? action()
            : Task.FromResult(Results.Problem($"Session '{id}' is not a hub session.", statusCode: StatusCodes.Status404NotFound));

    /// <summary>Runs a cloud call; auth and API failures become 502, a missing hub setting becomes 400.</summary>
    private static Task<IResult> Cloud(Func<Task<IResult>> action) => ApiErrors.RunAsync(async () =>
    {
        try
        {
            return await action();
        }
        catch (Exception e) when (e is ClaudeAuthMissingException or CloudSessionAuthException)
        {
            return Results.Problem("Signed out of Claude — run `claude auth login`", statusCode: StatusCodes.Status502BadGateway);
        }
        catch (Exception e) when (e is CloudSessionApiException or HttpRequestException)
        {
            return Results.Problem(e.Message, statusCode: StatusCodes.Status502BadGateway);
        }
        catch (TaskCanceledException e) when (e.InnerException is TimeoutException)
        {
            return Results.Problem("Claude did not respond in time.", statusCode: StatusCodes.Status502BadGateway);
        }
        catch (HubConfigException e)
        {
            return Results.Problem(e.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    });
}

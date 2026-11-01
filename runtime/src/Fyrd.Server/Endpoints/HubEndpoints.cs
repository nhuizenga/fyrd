using System.Text;
using System.Text.Json;
using Fyrd.Core.CloudSessions;
using Fyrd.Core.Hub;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace Fyrd.Server.Endpoints;

/// <summary>The console's hub API: state, Controller, hub-session actions, question answers, personas and hub memory.</summary>
internal static class HubEndpoints
{
    private const int MaxText = 20_000;
    private const string ResumeText = "Continue where you left off.";
    private const string NoController = "No Controller yet. Create a session on claude.ai and adopt it.";
    private const string NotPending = "This question is no longer waiting for an answer.";
    private const string SignedOut = "Signed out of Claude — run `claude auth login`";
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReopenDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(10);

    private sealed record TextBody(string? Text);
    private sealed record AdoptBody(string? SessionId);
    private sealed record AnswerBody(string? Choice, string? Text);

    public static IEndpointRouteBuilder MapHub(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/hub");

        group.MapGet("/state", (HubState state, ControllerManager controller) => ApiErrors.Run(() =>
        {
            // A Controller the last poll listed as live (a stored id archived elsewhere is a dead link), or one adopted
            // after the last poll, which no poll has had the chance to list yet.
            var stored = controller.CurrentId;
            var id = stored is not null
                     && (state.Sessions.Any(s => s.Id == stored)
                         || (controller.AdoptedAt is { } adoptedAt && (state.LastPollAt is not { } polled || polled < adoptedAt)))
                ? stored : null;
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

        // Fyrd never creates the Controller: the owner makes it on claude.ai (so it has connectors) and adopts it here.
        group.MapPost("/controller", (ControllerManager controller, CancellationToken ct) =>
            Cloud(async () => await controller.FindAsync(ct) is { } info
                ? Results.Ok(info)
                : Results.Problem(NoController, statusCode: StatusCodes.Status404NotFound)));

        group.MapPost("/controller/adopt", (AdoptBody? body, ControllerManager controller, HubState state, CancellationToken ct) =>
            Cloud(async () =>
            {
                var info = await controller.AdoptAsync(body?.SessionId ?? "", ct);
                state.RequestPoll();
                return Results.Ok(info);
            }));

        group.MapPost("/controller/retire", (ControllerManager controller, CancellationToken ct) =>
            Cloud(async () =>
            {
                await controller.RetireAsync(ct);
                return Results.NoContent();
            }));


        // Live transcript: relays the cloud session's own event stream as Server-Sent Events of TranscriptItems.
        group.MapGet("/sessions/{id}/stream", async (string id, HttpContext http, HubState state, ICloudSessions cloud,
            IOptions<JsonOptions> json) =>
        {
            if (!state.Sessions.Any(s => s.Id == id))
            {
                await Results.Problem($"Session '{id}' is not a hub session.", statusCode: StatusCodes.Status404NotFound)
                    .ExecuteAsync(http);
                return;
            }
            await StreamTranscript(id, http, cloud, json.Value.SerializerOptions);
        });

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
            if (!state.TryClaim(q.RequestId, q.SessionId))
                return Task.FromResult(Results.Problem(NotPending, statusCode: StatusCodes.Status409Conflict));
            return Cloud(async () =>
            {
                try
                {
                    await cloud.AnswerQuestionAsync(q.SessionId, q, progress.Answers!, ct);
                }
                // A cancelled or timed-out call may still have been delivered, so its claim is kept (the poll or TTL ends it).
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    state.Release(q.RequestId);
                    throw;
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

    /// <summary>
    /// Writes <c>event: item</c> frames until the client goes away, reopening the upstream stream whenever it ends.
    /// Resumes after <c>Last-Event-ID</c> (or <c>?after=</c>); drops sequence numbers already sent.
    /// </summary>
    private static async Task StreamTranscript(string id, HttpContext http, ICloudSessions cloud, JsonSerializerOptions options)
    {
        var ct = http.RequestAborted;
        long? last = long.TryParse(http.Request.Headers["Last-Event-ID"].ToString(), out var h) ? h
            : long.TryParse(http.Request.Query["after"].ToString(), out var q) ? q
            : null;

        var resp = http.Response;
        resp.ContentType = "text/event-stream";
        resp.Headers.CacheControl = "no-cache";
        resp.Headers["X-Accel-Buffering"] = "no";

        // Writes are serialized: the keep-alive timer and the relay loop share the response.
        var gate = new SemaphoreSlim(1, 1);
        var lastWrite = DateTimeOffset.UtcNow;
        async Task Write(string frame)
        {
            await gate.WaitAsync(ct);
            try
            {
                await resp.WriteAsync(frame, Encoding.UTF8, ct);
                await resp.Body.FlushAsync(ct);
                lastWrite = DateTimeOffset.UtcNow;
            }
            finally { gate.Release(); }
        }

        try
        {
            await Write(": connected\n\n");
            using var keepAliveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var keepAlive = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                while (await timer.WaitForNextTickAsync(keepAliveCts.Token))
                    if (DateTimeOffset.UtcNow - lastWrite >= KeepAlive) await Write(": keep-alive\n\n");
            }, keepAliveCts.Token);

            try
            {
                var delay = ReopenDelay;
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        await foreach (var e in cloud.StreamEventsAsync(id, last, ct))
                        {
                            if (last is { } seen && e.Sequence <= seen) continue;
                            last = e.Sequence;
                            foreach (var item in HubTranscript.Map(e))
                                await Write($"id: {item.Seq}\nevent: item\ndata: {JsonSerializer.Serialize(item, options)}\n\n");
                        }
                        delay = ReopenDelay;
                    }
                    catch (Exception e) when (e is ClaudeAuthMissingException or CloudSessionAuthException)
                    {
                        await Fault(SignedOut);
                        return;
                    }
                    // Any other failure (API error, dropped connection, missing setting) is reported, then retried.
                    catch (Exception e) when (!ct.IsCancellationRequested)
                    {
                        await Fault(e.Message);
                        delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxBackoff.Ticks));
                    }
                    await Task.Delay(delay, ct);
                }
            }
            finally
            {
                keepAliveCts.Cancel();
                try { await keepAlive; } catch { /* the client is gone or the relay ended; nothing to report */ }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The client went away.
        }

        Task Fault(string detail) =>
            Write($"event: fault\ndata: {JsonSerializer.Serialize(new { detail }, options)}\n\n");
    }

    private static IResult SavePersona(string? id, HubPersona? body, HubPersonas personas) => ApiErrors.Run(() =>
    {
        if (body is null) return Bad("Body is required.");
        // Locked is set only by seeding; the console cannot lock or unlock a persona.
        var p = body with { Id = id ?? body.Id ?? "", Locked = false };
        try { return Results.Ok(personas.Save(p)); }
        catch (InvalidOperationException e) { return Results.Problem(e.Message, statusCode: StatusCodes.Status409Conflict); }
    });

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

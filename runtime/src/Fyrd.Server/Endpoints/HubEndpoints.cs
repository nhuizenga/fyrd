using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using Fyrd.Core.CloudSessions;
using Fyrd.Core.Hub;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace Fyrd.Server.Endpoints;

/// <summary>The console's hub API: state, Controller, hub-session actions, question answers, personas and hub memory.</summary>
internal static partial class HubEndpoints
{
    private const int MaxText = 20_000;
    private const string ResumeText = "Continue where you left off.";
    private const int MaxTitle = 200;
    private const string NoController = "No Controller yet. Create one from the Sessions screen.";
    private const string NotPending = "This question is no longer waiting for an answer.";
    private const string SessionBlocked = "This session reached its daily spend limit.";
    private const string FyrdBlocked = "Fyrd reached its daily spend limit.";
    private const string SignedOut = "Signed out of Claude — run `claude auth login`";
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReopenDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(10);

    private sealed record TextBody(string? Text, string[]? FileIds = null);
    private sealed record BudgetBody(JsonElement? AmountUsd);
    private sealed record RenameBody(string? Title);
    private sealed record PrBody(string? Url, bool? Force = null);
    private sealed record AnswerBody(string? Choice, string? Text, string[]? FileIds = null);

    public static IEndpointRouteBuilder MapHub(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/hub");

        group.MapGet("/state", (HubState state, ControllerManager controller, PendingMemory pending, SessionProgress progress, GitHubSignIn gitHub, StackMergeJobs stackMerges) => ApiErrors.Run(() =>
        {
            // A Controller the last poll listed as live (a stored id archived elsewhere is a dead link), or one created
            // after the last poll, which no poll has had the chance to list yet.
            var stored = controller.CurrentId;
            var id = stored is not null
                     && (state.Sessions.Any(s => s.Id == stored)
                         || (controller.CreatedAt is { } createdAt && (state.LastPollAt is not { } polled || polled < createdAt)))
                ? stored : null;
            return Results.Ok(new
            {
                sessions = state.Sessions,
                questions = state.Questions,
                notifications = state.Notifications,
                signedOut = state.SignedOut,
                lastError = state.LastError,
                lastPollAt = state.LastPollAt,
                budget = state.Budget,
                pullRequests = state.PullRequests,
                stackMerges = stackMerges.Snapshot().Select(j => new
                {
                    j.Id, j.Repo, j.Prs, j.Index, status = j.Status.ToString().ToLowerInvariant(), j.Message, j.StartedAt, j.UpdatedAt,
                }),
                gitHub = new { configured = gitHub.Configured, login = gitHub.Login, error = gitHub.Error },
                pendingMemory = pending.Count,
                // Each session's current live-only progress line (a tool in use, the reply so far), for sessions nobody has open.
                progress = progress.Snapshot(),
                controller = id is null ? null : new { sessionId = id, url = "https://claude.ai/code/" + id },
            });
        }));

        group.MapPost("/controller", (ControllerManager controller, CancellationToken ct) =>
            Cloud(async () => await controller.FindAsync(ct) is { } info
                ? Results.Ok(info)
                : Results.Problem(NoController, statusCode: StatusCodes.Status404NotFound)));

        // Fyrd creates the Controller through the owner's claude CLI, so it is the owner's session and its opening prompt is
        // the owner's turn. Not tied to the request: a closed tab must not kill the CLI half way through creating it.
        group.MapPost("/controller/create", (ControllerManager controller, HubState state) =>
            Cloud(async () =>
            {
                var info = await controller.CreateAsync(CancellationToken.None);
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
            SessionProgress sessionProgress, IOptions<JsonOptions> json) =>
        {
            if (!state.Sessions.Any(s => s.Id == id))
            {
                await Results.Problem($"Session '{id}' is not a hub session.", statusCode: StatusCodes.Status404NotFound)
                    .ExecuteAsync(http);
                return;
            }
            await StreamTranscript(id, http, cloud, sessionProgress, json.Value.SerializerOptions);
        });

        // Raw event export: every saved event as JSONL (header line, one raw event per line, trailer line).
        group.MapGet("/sessions/{id}/export", (string id, HubState state, ICloudSessions cloud, TimeProvider time,
            ILoggerFactory logs, CancellationToken ct) =>
            InHub(id, state, () => Cloud(async () =>
            {
                var view = state.Sessions.First(s => s.Id == id);
                var now = time.GetUtcNow();
                // Everything that can fail early (session lookup, first page) runs before any byte is written,
                // so it comes back as an ordinary problem response.
                var events = cloud.AllEventsAsync(id, null, 100, ct).GetAsyncEnumerator(ct);
                try
                {
                    var session = await cloud.GetAsync(id, ct);
                    var hasFirst = await events.MoveNextAsync();
                    return new ExportResult(id, view, session, events, hasFirst, now, logs.CreateLogger("Fyrd.Export"));
                }
                catch
                {
                    await events.DisposeAsync();
                    throw;
                }
            })));

        // An owner-attached file goes to the Files API now (expiring in an hour); its id waits here until the message or
        // answer that carries it is sent.
        group.MapPost("/uploads", (HttpRequest request, IFiles files, UploadStore uploads, CancellationToken ct) =>
            Cloud(async () =>
            {
                request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>()
                    ?.MaxRequestBodySize = UploadStore.MaxBytes + 1024 * 1024;
                if (!request.HasFormContentType) return Bad("Send the file as multipart form data.");
                IFormFile? file;
                try { file = (await request.ReadFormAsync(ct)).Files.GetFile("file"); }
                catch (Exception e) when (e is InvalidDataException or BadHttpRequestException or IOException)
                {
                    return Results.Problem($"The file is too large (the limit is {UploadStore.MaxBytes / 1024 / 1024} MB).", statusCode: StatusCodes.Status413PayloadTooLarge);
                }
                if (file is null) return Bad("Body { file } is required.");
                if (file.Length == 0) return Bad("That file is empty.");
                if (file.Length > UploadStore.MaxBytes)
                    return Results.Problem($"The file is too large (the limit is {UploadStore.MaxBytes / 1024 / 1024} MB).", statusCode: StatusCodes.Status413PayloadTooLarge);
                if (!uploads.HasRoom(file.Length))
                    return Results.Problem("Too many attached files are waiting to be sent — send or remove some first.", statusCode: StatusCodes.Status429TooManyRequests);
                await using var content = file.OpenReadStream();
                var uploaded = await files.UploadAsync(content, Path.GetFileName(file.FileName), file.ContentType, ct);
                uploads.Add(uploaded);
                return Results.Ok(new { id = uploaded.Id, name = uploaded.Filename, mimeType = uploaded.MimeType, size = uploaded.SizeBytes });
            }));

        // Removing a chip in the console forgets the id (the file itself expires on its own).
        group.MapDelete("/uploads/{fileId}", (string fileId, UploadStore uploads) =>
        {
            uploads.Forget(fileId);
            return Results.NoContent();
        });

        // The owner typed this, so it goes through the owner's CLI and reads as theirs, not as a peer message.
        // The owner read it: gone now, and the next poll does not bring it back.
        group.MapPost("/notifications/{id}/dismiss", (string id, HubState state, NotificationDismissals dismissals) => ApiErrors.Run(() =>
        {
            if (!state.Notifications.Any(n => n.Id == id))
                return Results.Problem("That notification is not showing.", statusCode: StatusCodes.Status404NotFound);
            dismissals.Dismiss(id);
            state.DropNotification(id);
            return Results.NoContent();
        }));

        group.MapPost("/sessions/{id}/message", (string id, TextBody? body, HubState state, IOwnerMessages owner, UploadStore uploads, NotificationDismissals dismissals, CancellationToken ct) =>
            InHub(id, state, () =>
            {
                if (BlockedError(id, state) is { } blocked) return Task.FromResult(blocked);
                if (FilesError(body?.FileIds, uploads, out var files) is { } fileError) return Task.FromResult(fileError);
                // With files the text may be empty (the files are the message); without them it is required.
                if (files.Count == 0 || !string.IsNullOrWhiteSpace(body?.Text))
                    if (TextError(body?.Text) is { } error) return Task.FromResult(error);
                var message = Attachments.Append(body?.Text?.Trim() ?? "", files);
                if (message.Length > MaxText) return Task.FromResult(Bad($"Text and attachment notes are longer than {MaxText} characters."));
                return Cloud(async () =>
                {
                    // Taking the ids and sending are separate steps so a failed send hands the files back for a retry.
                    if (uploads.TryClaim(body?.FileIds ?? []) is not { } claim)
                        return Bad("An attached file was already sent or has expired — attach it again.");
                    try
                    {
                        await owner.SendAsync(id, message, ct);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        claim.Restore();
                        throw;
                    }
                    // A message answers whatever the session was waiting on.
                    foreach (var dropped in state.DropNotificationsFor(id)) dismissals.Dismiss(dropped);
                    return Results.NoContent();
                });
            }));

        group.MapPost("/sessions/{id}/pause", (string id, HubState state, ICloudSessions cloud, CancellationToken ct) =>
            InHub(id, state, () => Cloud(async () =>
            {
                await cloud.InterruptAsync(id, ct);
                return Results.NoContent();
            })));

        // The console's trash button: archives the session; the next poll drops it from the list.
        group.MapPost("/sessions/{id}/archive", (string id, HubState state, ICloudSessions cloud, CancellationToken ct) =>
            InHub(id, state, () => Cloud(async () =>
            {
                await cloud.ArchiveAsync(id, ct);
                state.RequestPoll();
                return Results.NoContent();
            })));

        // The console's pencil button: renames the session on claude.ai; the next poll shows the new title.
        group.MapPost("/sessions/{id}/rename", (string id, RenameBody? body, HubState state, ICloudSessions cloud, CancellationToken ct) =>
            InHub(id, state, () => Cloud(async () =>
            {
                var title = body?.Title?.Trim();
                if (string.IsNullOrEmpty(title))
                    return Results.Problem("Title is required.", statusCode: StatusCodes.Status400BadRequest);
                if (title.Length > MaxTitle)
                    return Results.Problem($"Title is at most {MaxTitle} characters.", statusCode: StatusCodes.Status400BadRequest);
                if (state.Sessions.First(s => s.Id == id).Role == "controller")
                    return Results.Problem("The Controller can't be renamed.", statusCode: StatusCodes.Status409Conflict);
                await cloud.RenameAsync(id, title, ct);
                state.RequestPoll();
                return Results.NoContent();
            })));

        // Pull requests the session opened: live status from GitHub, and the owner's ready / merge buttons.
        group.MapGet("/sessions/{id}/pull-requests", (string id, HubState state, IGitHub github, CancellationToken ct) =>
            InHub(id, state, async () =>
            {
                if (!github.Configured) return NotConfigured();
                var urls = state.Sessions.First(s => s.Id == id).PullRequests;
                var all = await Task.WhenAll(urls.Select(async url =>
                {
                    try { return await github.GetAsync(url, ct); }
                    catch (GitHubException e) { return new PullRequestStatus(url, 0, "", "unknown", false, "none", e.Message); }
                }));
                return Results.Ok(all.Select(p => new
                {
                    p.Url, p.Number, p.Title, p.State, p.Draft, p.Checks, p.Error, canMerge = p.CanMerge,
                    p.Base, p.Head, p.ChecksPassed, p.ChecksFailed, p.ChecksPending, p.FailingCheck, p.FailingChecks,
                    p.Review, p.Mergeable, p.MergeState,
                }));
            }));

        group.MapPost("/sessions/{id}/pull-requests/ready", (string id, PrBody? body, HubState state, IGitHub github, OwnedPullRequests owned, CancellationToken ct) =>
            PrAction(id, body, state, github, owned, ct, async (url, current) =>
            {
                if (!current.Draft || current.State != "open") return Results.Problem("That pull request is not a draft.", statusCode: StatusCodes.Status409Conflict);
                await github.MarkReadyAsync(url, ct);
                return Results.NoContent();
            }));

        // Merges a whole chain of stacked PRs, top down, waiting for checks between steps (a background job).
        group.MapPost("/sessions/{id}/pull-requests/merge-stack", (string id, PrBody? body, HubState state, IGitHub github, StackMergeJobs jobs, CancellationToken ct) =>
            InHub(id, state, async () =>
            {
                if (!github.Configured) return NotConfigured();
                var url = body?.Url;
                var own = state.Sessions.First(s => s.Id == id).PullRequests;
                if (!PullRequestUrl.IsValid(url) || !own.Contains(url!))
                    return Bad("That is not one of this session's pull requests.");
                try
                {
                    // Live status for every PR of the session, so a stale console view cannot start a bad merge.
                    var live = await Task.WhenAll(own.Select(u => github.GetAsync(u, ct)));
                    if (PullRequestStacks.Chain(live, url!) is not { } chain)
                        return Results.Problem("That pull request is not stacked with another one.", statusCode: StatusCodes.Status409Conflict);
                    if (StackMergeJobs.Preconditions(chain.RootToTop, chain.Fork, own) is { } why)
                        return Results.Problem(why, statusCode: StatusCodes.Status409Conflict);
                    var (owner, repo, _) = PullRequestUrl.Parse(url!);
                    return jobs.Start($"{owner}/{repo}", chain.RootToTop, out var conflict) is { } job
                        ? Results.Accepted(value: new { jobId = job.Id })
                        : Results.Problem(conflict, statusCode: StatusCodes.Status409Conflict);
                }
                catch (GitHubException e)
                {
                    return Results.Problem(e.Message, statusCode: StatusCodes.Status502BadGateway);
                }
            }));

        group.MapPost("/stack-merges/{jobId}/cancel", (string jobId, StackMergeJobs jobs) =>
            jobs.Cancel(jobId)
                ? Results.NoContent()
                : Results.Problem("No such stack merge.", statusCode: StatusCodes.Status404NotFound));

        group.MapPost("/sessions/{id}/pull-requests/merge", (string id, PrBody? body, HubState state, IGitHub github, OwnedPullRequests owned, CancellationToken ct) =>
            PrAction(id, body, state, github, owned, ct, async (url, current) =>
            {
                // Re-checked here, not trusted from the console: the PR may have changed since it was listed.
                if (!current.CanMerge) return Results.Problem("That pull request is not ready to merge (draft, checks not passing, merge conflicts, or not open).", statusCode: StatusCodes.Status409Conflict);
                // Merge from the top of a stack down. Only "merge this only anyway" (force) may skip the order.
                if (body?.Force != true
                    && PullRequestStacks.MergeOrderWarning(StackedStatuses(state, id, current), current) is { } warning)
                    return Results.Problem(warning, statusCode: StatusCodes.Status409Conflict);
                await github.MergeAsync(url, current.HeadSha, ct);
                return Results.NoContent();
            }));

        group.MapPost("/sessions/{id}/resume", (string id, HubState state, IOwnerMessages owner, CancellationToken ct) =>
            InHub(id, state, () => BlockedError(id, state) is { } blocked
                ? Task.FromResult(blocked)
                : Cloud(async () =>
                {
                    await owner.SendAsync(id, ResumeText, ct);
                    return Results.NoContent();
                })));

        // Raises one session's limit for today. Only +$25 and +$100, and only once the session has used 90% of its limit.
        group.MapPost("/sessions/{id}/budget", (string id, BudgetBody? body, HubState state, CostGuard guard) =>
            InHub(id, state, () => Task.FromResult(ApiErrors.Run(() =>
            {
                if (body?.AmountUsd is not { ValueKind: JsonValueKind.Number } n || !n.TryGetDecimal(out var amount)
                    || !CostGuard.ExtendAmounts.Contains(amount))
                    return Bad("amountUsd must be 25 or 100.");
                // One snapshot of the session list: the poll swaps it as a whole.
                var ids = state.Sessions.Select(s => s.Id).ToList();
                if (!ids.Contains(id))
                    return Results.Problem($"Session '{id}' is not a hub session.", statusCode: StatusCodes.Status404NotFound);
                var (status, view) = guard.TryExtend(id, amount, ids);
                return status switch
                {
                    CostGuard.ExtendStatus.Ok => RaisedOk(state, view),
                    CostGuard.ExtendStatus.NoData => Results.Problem("This session has no cost data yet.", statusCode: StatusCodes.Status409Conflict),
                    CostGuard.ExtendStatus.Exempt => Results.Problem("The Controller has no spend limit to raise.", statusCode: StatusCodes.Status409Conflict),
                    CostGuard.ExtendStatus.FyrdBlocked => Results.Problem("Fyrd reached its daily spend limit; raising one session does not lift it.", statusCode: StatusCodes.Status409Conflict),
                    _ => Results.Problem("A limit can be raised once 90% of it is used.", statusCode: StatusCodes.Status409Conflict),
                };
            }))));

        group.MapPost("/questions/{id}/answer", (string id, AnswerBody? body, HubState state, ICloudSessions cloud, UploadStore uploads, CancellationToken ct) =>
        {
            var q = state.Questions.FirstOrDefault(x => x.Id == id);
            if (q is null) return Task.FromResult(Results.Problem(NotPending, statusCode: StatusCodes.Status409Conflict));
            // Answering resumes the session, so a budget block applies here too.
            if (BlockedError(q.SessionId, state) is { } blocked) return Task.FromResult(blocked);

            if (FilesError(body?.FileIds, uploads, out var files) is { } fileError) return Task.FromResult(fileError);
            var hasChoice = !string.IsNullOrEmpty(body?.Choice);
            if (hasChoice && files.Count > 0) return Task.FromResult(Bad("Files go with a typed answer, not an option."));
            // An answer with files is always free text (a typed answer, or just the files).
            var hasText = !string.IsNullOrWhiteSpace(body?.Text) || (files.Count > 0 && !hasChoice);
            if (hasChoice == hasText)
                return Task.FromResult(Bad("Send exactly one of choice or text."));
            if (hasChoice && !q.Options.Contains(body!.Choice!))
                return Task.FromResult(Bad($"'{body.Choice}' is not one of this question's options."));
            if (hasText && body?.Text?.Length > MaxText)
                return Task.FromResult(Bad($"Text is longer than {MaxText} characters."));

            // One can_use_tool request can carry several questions; it gets exactly one control_response with all answers.
            var answer = hasChoice ? body!.Choice! : Attachments.Append(body?.Text?.Trim() ?? "", files);
            if (answer.Length > MaxText)
                return Task.FromResult(Bad($"Text and attachment notes are longer than {MaxText} characters."));

            // The files are taken now (recorded in the answer text), so they cannot be reused by a later request; they
            // go back to the store if this answer turns out not to be accepted or its send fails.
            if (uploads.TryClaim(body?.FileIds ?? []) is not { } fileClaim)
                return Task.FromResult(Bad("An attached file was already sent or has expired — attach it again."));
            var progress = state.RecordAnswer(q.Id, answer);
            if (!progress.Pending)
            {
                fileClaim.Restore();
                return Task.FromResult(Results.Problem(NotPending, statusCode: StatusCodes.Status409Conflict));
            }
            if (!progress.Ready)
                return Task.FromResult(Results.Json(new { waitingFor = progress.WaitingFor }, statusCode: StatusCodes.Status202Accepted));
            if (!state.TryClaim(q.RequestId, q.SessionId))
            {
                fileClaim.Restore();
                return Task.FromResult(Results.Problem(NotPending, statusCode: StatusCodes.Status409Conflict));
            }
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
                    fileClaim.Restore();
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

        group.MapPost("/personas/{id}/reset", (string id, HubPersonas personas) => ApiErrors.Run(() =>
        {
            try { return Results.Ok(personas.Reset(id)); }
            catch (KeyNotFoundException e) { return Results.Problem(e.Message, statusCode: StatusCodes.Status404NotFound); }
        }));

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

        group.MapGet("/memory/pending", (PendingMemory pending) => ApiErrors.Run(() => Results.Ok(pending.List())));

        group.MapPost("/memory/pending/{id}/approve", (string id, TextBody? body, PendingMemory pending, TimeProvider time) =>
            ApiErrors.Run(() =>
            {
                // Send {} to approve as proposed, or { text } to approve an edited version (same checks as any memory edit).
                if (body?.Text is not null && (string.IsNullOrWhiteSpace(body.Text) || body.Text.Length > PendingMemory.MaxTextLength))
                    return Bad($"Text must be 1 to {PendingMemory.MaxTextLength} characters.");
                return Results.Ok(pending.Approve(id, body?.Text?.Trim(), time.GetUtcNow()));
            }));

        group.MapPost("/memory/pending/{id}/reject", (string id, PendingMemory pending) => ApiErrors.Run(() =>
        {
            pending.Reject(id);
            return Results.NoContent();
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
    private static async Task StreamTranscript(string id, HttpContext http, ICloudSessions cloud, SessionProgress sessionProgress,
        JsonSerializerOptions options)
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

        var progress = new ProgressTracker();
        var workerToolIds = new HashSet<string>(StringComparer.Ordinal);
        string Progress(TranscriptItem item) => $"event: progress\ndata: {JsonSerializer.Serialize(item, options)}\n\n";

        try
        {
            await Write(": connected\n\n");
            // Live-only progress is never replayed upstream: start from what the watcher has kept since before this connect.
            if (sessionProgress.Get(id) is { } kept) await Write(Progress(kept));
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
                            if (e.Ephemeral)
                            {
                                // Live-only: no id (so EventSource's resume point is untouched) and never deduplicated.
                                if (progress.Apply(e) is { } live) await Write(Progress(live));
                                continue;
                            }
                            if (last is { } seen && e.Sequence <= seen) continue;
                            last = e.Sequence;
                            foreach (var item in HubTranscript.Map(e, workerToolIds))
                                await Write($"id: {item.Seq}\nevent: item\ndata: {JsonSerializer.Serialize(item, options)}\n\n");
                            progress.ApplySaved(e);
                            // A saved event (a message, a turn result) supersedes whatever was streaming.
                            if (progress.Reset(e.Event.CreatedAt) is { } cleared) await Write(Progress(cleared));
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

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex SlugRegex();

    /// <summary><c>&lt;title-slug&gt;-&lt;session id&gt;-&lt;yyyyMMdd-HHmmss&gt;.jsonl</c>, safe for a header and a file system.</summary>
    internal static string ExportFileName(string title, string id, DateTimeOffset at)
    {
        var slug = SlugRegex().Replace(title.ToLowerInvariant(), "-").Trim('-');
        if (slug.Length > 60) slug = slug[..60].Trim('-');
        if (slug.Length == 0) slug = "session";
        var safeId = Regex.Replace(id, "[^A-Za-z0-9_-]", "_");
        return $"{slug}-{safeId}-{at.UtcDateTime:yyyyMMdd-HHmmss}.jsonl";
    }

    /// <summary>Streams the export after the early calls succeeded; a later failure aborts the connection.</summary>
    private sealed class ExportResult(string id, HubSessionView view, CloudSession session, IAsyncEnumerator<SessionEvent> events,
        bool hasFirst, DateTimeOffset at, ILogger log) : IResult
    {
        public async Task ExecuteAsync(HttpContext http)
        {
            var ct = http.RequestAborted;
            var name = ExportFileName(view.Title, id, at);
            var resp = http.Response;
            resp.ContentType = "application/x-ndjson; charset=utf-8";
            resp.Headers.ContentDisposition = $"attachment; filename=\"{name}\"; filename*=UTF-8''{Uri.EscapeDataString(name)}";
            resp.Headers.CacheControl = "no-store";
            var utf8 = new UTF8Encoding(false);
            try
            {
                var header = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["type"] = "fyrd_export", ["version"] = 1, ["exportedAt"] = at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                    ["sessionId"] = id, ["title"] = view.Title, ["role"] = string.IsNullOrEmpty(view.Role) ? null : view.Role,
                    ["session"] = session.Raw,
                });
                await resp.WriteAsync(header + "\n", utf8, ct);
                var count = 0;
                var inPage = 0;
                var more = hasFirst;
                while (more)
                {
                    await resp.WriteAsync(JsonSerializer.Serialize(events.Current.Raw) + "\n", utf8, ct);
                    count++;
                    if (++inPage >= 100) { inPage = 0; await resp.Body.FlushAsync(ct); }
                    more = await events.MoveNextAsync();
                }
                await resp.WriteAsync($"{{\"type\":\"fyrd_export_end\",\"eventCount\":{count}}}\n", utf8, ct);
                await resp.Body.FlushAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The client went away.
            }
            catch (Exception e)
            {
                // Headers are sent: abort so the download fails instead of saving a truncated file that looks whole.
                log.LogError(e, "Export of {SessionId} failed part-way", id);
                http.Abort();
            }
            finally
            {
                await events.DisposeAsync();
            }
        }
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

    /// <summary>400 when the ids are too many, blank, or not files Fyrd uploaded in the last hour; otherwise the files in order (nothing is taken yet).</summary>
    private static IResult? FilesError(string[]? ids, UploadStore uploads, out IReadOnlyList<UploadedFile> files)
    {
        files = [];
        if (ids is null || ids.Length == 0) return null;
        // Bound the raw array before doing any work on it, then refuse null / blank entries.
        if (ids.Length > UploadStore.MaxFiles) return Bad($"Attach at most {UploadStore.MaxFiles} files.");
        if (ids.Any(string.IsNullOrWhiteSpace)) return Bad("File ids must be non-empty strings.");
        var (found, missing) = uploads.Resolve(ids);
        if (found is null) return Bad($"File '{missing}' is not an uploaded file (uploads are kept for an hour).");
        files = found;
        return null;
    }

    /// <summary>409 when the last poll blocked this session (its own limit, or the fyrd's); the Controller is never blocked.</summary>
    private static IResult? BlockedError(string id, HubState state) =>
        state.Sessions.FirstOrDefault(s => s.Id == id)?.Cost?.Blocked switch
        {
            "session" => Results.Problem(SessionBlocked, statusCode: StatusCodes.Status409Conflict),
            "fyrd" => Results.Problem(FyrdBlocked, statusCode: StatusCodes.Status409Conflict),
            _ => null,
        };

    private static IResult RaisedOk(HubState state, HubCostView? view)
    {
        state.RequestPoll();
        return Results.Ok(view);
    }

    /// <summary>Runs a PR action on one of this session's own pull requests, with its freshly read status.</summary>
    private static Task<IResult> PrAction(string id, PrBody? body, HubState state, IGitHub github, OwnedPullRequests owned, CancellationToken ct,
        Func<string, PullRequestStatus, Task<IResult>> action) =>
        InHub(id, state, async () =>
        {
            if (!github.Configured) return NotConfigured();
            var url = body?.Url;
            if (!PullRequestUrl.IsValid(url) || !state.Sessions.First(s => s.Id == id).PullRequests.Contains(url!))
                return Bad("That is not one of this session's pull requests.");
            try
            {
                var result = await action(url!, await github.GetAsync(url!, ct));
                // The next poll lists the repo again, so the new state shows without waiting for the cache.
                if (result is IStatusCodeHttpResult { StatusCode: 204 })
                {
                    var (owner, repo, _) = PullRequestUrl.Parse(url!);
                    owned.Invalidate($"{owner}/{repo}");
                    state.RequestPoll();
                }
                return result;
            }
            catch (GitHubException e)
            {
                return Results.Problem(e.Message, statusCode: StatusCodes.Status502BadGateway);
            }
        });

    /// <summary>The session's PRs as the last poll listed them, with the freshly read one in place of its listed copy.</summary>
    private static IEnumerable<PullRequestStatus> StackedStatuses(HubState state, string id, PullRequestStatus current) =>
        state.Sessions.First(s => s.Id == id).PullRequests
            .Select(u => u == current.Url ? current : state.PullRequests.GetValueOrDefault(u))
            .OfType<PullRequestStatus>();

    private static IResult NotConfigured() =>
        Results.Problem("GitHub is not configured. Set GH_TOKEN before /fyrd:up.", statusCode: StatusCodes.Status503ServiceUnavailable);

    private static IResult Bad(string detail) => Results.Problem(detail, statusCode: StatusCodes.Status400BadRequest);

    /// <summary>Only sessions the last hub poll saw may be driven from the console.</summary>
    private static Task<IResult> InHub(string id, HubState state, Func<Task<IResult>> action) =>
        state.Sessions.Any(s => s.Id == id)
            ? action()
            : Task.FromResult(Results.Problem($"Session '{id}' is not a hub session.", statusCode: StatusCodes.Status404NotFound));

    /// <summary>Runs a cloud call; auth and API failures become 502, a missing hub setting becomes 400, a Controller create
    /// already running becomes 409.</summary>
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
        catch (Exception e) when (e is CloudSessionApiException or HttpRequestException or OwnerMessageException)
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
        catch (ControllerBusyException e)
        {
            return Results.Problem(e.Message, statusCode: StatusCodes.Status409Conflict);
        }
    });
}

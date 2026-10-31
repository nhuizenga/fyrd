using System.Text.Json;
using Fyrd.Core.CloudSessions;
using Fyrd.Core.Specs;

namespace Fyrd.Core.Orchestration;

/// <summary>
/// Polls each active spec's lead session and peer sessions and turns what it sees into units, inbox cards and
/// spec status. Passes must run sequentially: the inbox's HasSourceKey-then-Add is not atomic.
/// </summary>
public sealed class SpecRunner(
    SpecStore specs, InboxStore inbox, ICloudSessions cloud, RunnerState state, TimeProvider time, LeadRelay relay)
{
    private static readonly TimeSpan StaleLaunch = TimeSpan.FromMinutes(2);

    private const string AttentionPrefix = "Lead needs attention: ";

    public async Task PollOnceAsync(CancellationToken ct)
    {
        string? lastError = null;
        string? warning = null;
        foreach (var spec in specs.ListActive())
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(spec.SessionId)) continue;
            try
            {
                warning ??= await PollSpecAsync(spec, ct);
            }
            catch (Exception e) when (e is CloudSessionAuthException or ClaudeAuthMissingException)
            {
                state.SignedOut = true;
                state.LastPollAt = time.GetUtcNow();
                return;
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                lastError = $"{spec.Id}: {e.Message}";
            }
        }
        lastError ??= await RetryRelaysAsync(ct);
        lastError ??= await ReconcileLaunchesAsync(ct);
        state.SignedOut = false;
        state.LastError = lastError ?? warning;
        state.LastPollAt = time.GetUtcNow();
    }

    /// <summary>Re-sends answers whose delivery is pending or failed; returns the first error message, or null.</summary>
    private async Task<string?> RetryRelaysAsync(CancellationToken ct)
    {
        string? error = null;
        foreach (var card in inbox.ListRelaysToRetry())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var e = await relay.DeliverAsync(card, ct);
                if (e is not null) error ??= $"{card.SpecId}: relay failed: {e}";
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                error ??= $"{card.SpecId}: relay failed: {e.Message}";
            }
        }
        return error;
    }

    /// <summary>Adopts a session started by an interrupted launch, or sends the spec back to Draft.</summary>
    private async Task<string?> ReconcileLaunchesAsync(CancellationToken ct)
    {
        string? error = null;
        foreach (var run in specs.ListStaleLaunches(StaleLaunch))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var found = (await cloud.ListByTagAsync("fyrd-spec-" + run.Id, ct))
                    .Where(s => s.SessionStatus != "archived" && s.Tags.Contains("fyrd-role-lead"))
                    .OrderByDescending(CreatedAt)
                    .FirstOrDefault();
                if (found is not null) specs.SetLead(run.Id, found.Id);
                else specs.Finish(run.Id, SpecStatus.Draft, null, "Launch interrupted; try again");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                error ??= $"{run.Id}: {e.Message}";
            }
        }
        return error;
    }

    /// <summary>Returns a short note about ignored malformed protocol blocks, or null.</summary>
    private async Task<string?> PollSpecAsync(SpecRun spec, CancellationToken ct)
    {
        var lead = spec.SessionId!;

        // 1. Events
        var events = new List<SessionEvent>();
        var after = spec.LastEventId;
        while (true)
        {
            var page = await cloud.ListEventsAsync(lead, 100, after, null, ct);
            events.AddRange(page.Events);
            if (!page.HasMore || page.LastId is null || page.LastId == after) break;
            after = page.LastId;
        }

        var finished = false;
        string? warning = null;
        foreach (var ev in events.Where(e => e.Type == "assistant"))
        {
            var parsed = Protocol.Parse(ev.Text ?? "");
            if (!string.IsNullOrEmpty(ev.Text)) state.SetQuestionFlag(spec.Id, parsed.Questions.Count > 0);
            if (parsed.Warnings.Count > 0)
                warning ??= $"Ignored malformed fyrd block in {spec.Id}: {parsed.Warnings[0]}";
            if (parsed.Units is not null) ApplyUnits(spec.Id, parsed.Units);
            foreach (var q in parsed.Questions) AddQuestion(spec.Id, q);
            if (parsed.Done is { } done)
            {
                specs.Finish(spec.Id, SpecStatus.ReadyForHandoff, done.Branch, null);
                finished = true;
            }
            else if (parsed.Failed is { } failed)
            {
                specs.Finish(spec.Id, SpecStatus.Failed, null, failed.Reason);
                finished = true;
            }
        }
        var cursor = events.Count > 0 ? events[^1].Uuid : spec.LastEventId;
        if (events.Count > 0) specs.SetCursor(spec.Id, cursor!);
        if (finished) return warning;

        // 2. Lead status
        var leadSession = await cloud.GetAsync(lead, ct);
        state.Record(leadSession);
        var needsAction = leadSession.Summary?.NeedsAction;
        if (leadSession.SessionStatus == "idle" && !string.IsNullOrWhiteSpace(needsAction)
            && leadSession.Summary?.StatusCategory != "completed"
            && cursor is not null && !state.LastTurnHadQuestion(spec.Id)
            && !inbox.ListOpenFor(spec.Id).Any(c => c.SourceKey is not null && !c.SourceKey.Contains(":attention:")))
        {
            var key = $"{spec.Id}:attention:{cursor}";
            if (!inbox.HasSourceKey(key))
                inbox.Add(spec.Id, null, AttentionPrefix + needsAction, [], null, null,
                    $"https://claude.ai/code/{lead}", true, key);
        }

        // 3. Peers
        var peers = (await cloud.ListByTagAsync("fyrd-spec-" + spec.Id, ct))
            .Where(p => p.SessionStatus != "archived").ToList();
        foreach (var p in peers) state.Record(p);
        ApplyPeers(spec.Id, peers);
        return warning;
    }

    private void ApplyUnits(string specId, IReadOnlyList<UnitDecl> decls)
    {
        var existing = specs.ListUnits(specId).ToDictionary(u => u.Id);
        var units = new List<Unit>();
        foreach (var d in decls)
        {
            existing.TryGetValue(d.Id, out var old);
            units.Add(d.Done
                ? new Unit(d.Id, specId, d.Title, UnitStage.Done, UnitState.Done)
                : new Unit(d.Id, specId, d.Title, old?.Stage ?? UnitStage.Implement, old?.State ?? UnitState.Idle));
        }
        specs.WriteUnits(specId, units);
    }

    private void AddQuestion(string specId, QuestionDecl q)
    {
        var key = $"{specId}:{q.Id}";
        if (inbox.HasSourceKey(key)) return;
        inbox.Add(specId, q.Unit, q.Question, q.Options, q.Recommendation, q.Reason, q.Details, true, key);
    }

    private void ApplyPeers(string specId, IReadOnlyList<CloudSession> peers)
    {
        var units = specs.ListUnits(specId);
        if (units.Count == 0) return;
        var openUnitIds = inbox.ListOpenFor(specId).Select(c => c.UnitId).Where(u => u is not null).ToHashSet();

        var newestByUnit = new Dictionary<string, (CloudSession Session, DateTimeOffset Created, string Role)>();
        foreach (var p in peers)
        {
            var unitId = TagValue(p, "fyrd-unit-");
            var role = TagValue(p, "fyrd-role-");
            if (unitId is null || role is null) continue;
            var created = CreatedAt(p);
            if (!newestByUnit.TryGetValue(unitId, out var cur) || created >= cur.Created)
                newestByUnit[unitId] = (p, created, role);
        }

        var updated = new List<Unit>();
        foreach (var u in units)
        {
            if (u.Stage == UnitStage.Done || !newestByUnit.TryGetValue(u.Id, out var n))
            {
                updated.Add(u);
                continue;
            }
            var stage = n.Role switch
            {
                "implementer" => UnitStage.Implement,
                "reviewer" => UnitStage.Review,
                "validator" => UnitStage.Validate,
                _ => u.Stage,
            };
            var unitState = openUnitIds.Contains(u.Id) ? UnitState.WaitingOnOwner
                : n.Session.SessionStatus == "idle" ? UnitState.Idle
                : UnitState.Running;
            updated.Add(u with { Stage = stage, State = unitState });
        }
        specs.WriteUnits(specId, updated);
    }

    private static string? TagValue(CloudSession s, string prefix) =>
        s.Tags.FirstOrDefault(t => t.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];

    private static DateTimeOffset CreatedAt(CloudSession s) =>
        s.Raw.ValueKind == JsonValueKind.Object
        && s.Raw.TryGetProperty("created_at", out var v) && v.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(v.GetString(), out var dt)
            ? dt : DateTimeOffset.MinValue;
}

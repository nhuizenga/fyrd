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
        var relayErr = await RetryRelaysAsync(ct);
        var launchErr = await ReconcileLaunchesAsync(ct);
        lastError ??= relayErr ?? launchErr;
        state.SignedOut = false;
        state.LastError = lastError ?? warning;
        state.LastPollAt = time.GetUtcNow();
    }

    /// <summary>Re-sends answers whose delivery is pending or failed; returns the first error message, or null.</summary>
    private async Task<string?> RetryRelaysAsync(CancellationToken ct)
    {
        string? error = null;
        var active = specs.ListActive().Where(s => !string.IsNullOrEmpty(s.SessionId)).Select(s => s.Id).ToHashSet();
        foreach (var card in inbox.ListRelaysToRetry())
        {
            ct.ThrowIfCancellationRequested();
            if (!active.Contains(card.SpecId)) continue;
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
                var researching = run.Status == SpecStatus.Researching;
                var role = researching ? "fyrd-role-research-lead" : "fyrd-role-lead";
                var found = (await cloud.ListByTagAsync("fyrd-spec-" + run.Id, ct))
                    .Where(s => s.SessionStatus != "archived" && s.Tags.Contains(role))
                    .OrderByDescending(CreatedAt)
                    .FirstOrDefault();
                if (found is not null)
                {
                    if (researching) specs.SetResearchLead(run.Id, found.Id);
                    else specs.SetLead(run.Id, found.Id);
                }
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

        var researching = spec.Status == SpecStatus.Researching;
        var finished = false;
        string? warning = null;
        foreach (var ev in events.Where(e => e.Type == "assistant"))
        {
            var parsed = Protocol.Parse(ev.Text ?? "");
            if (!string.IsNullOrEmpty(ev.Text)) state.SetQuestionFlag(spec.Id, parsed.Questions.Count > 0);
            if (parsed.Warnings.Count > 0)
                warning ??= $"Ignored malformed fyrd block in {spec.Id}: {parsed.Warnings[0]}";
            if (researching)
            {
                if (parsed.Units is not null || parsed.Done is not null)
                    warning ??= $"Ignored fyrd-units/fyrd-done from research lead in {spec.Id}";
            }
            else
            {
                if (parsed.SpecDraft is not null || parsed.SpecDraftInvalid)
                    warning ??= $"Ignored fyrd-spec-draft from build lead in {spec.Id}";
                if (parsed.Units is not null) ApplyUnits(spec.Id, parsed.Units);
            }
            foreach (var q in parsed.Questions) AddQuestion(spec.Id, q);
            if (researching)
            {
                if (parsed.SpecDraft is { } sd)
                {
                    specs.SaveProposal(spec.Id, sd.Markdown, sd.Summary ?? "", sd.OpenQuestions, lead);
                    specs.Finish(spec.Id, SpecStatus.Draft, null, null);
                    finished = true;
                }
                else if (parsed.SpecDraftInvalid)
                {
                    specs.Finish(spec.Id, SpecStatus.Draft, null, "Research returned no spec");
                    finished = true;
                }
                else if (parsed.Failed is { } rf)
                {
                    specs.Finish(spec.Id, SpecStatus.Draft, null, rf.Reason);
                    finished = true;
                }
                if (finished) break;
            }
            else if (parsed.Done is { } done)
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
        if (finished)
        {
            if (researching) CompleteResearchUnits(spec.Id);
            return warning;
        }

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
        if (researching) ApplyResearchPeers(spec.Id, peers);
        else ApplyPeers(spec.Id, peers);
        return warning;
    }

    private static readonly string[] ResearchAngles = ["codebase", "approach", "risks"];

    private void ApplyResearchPeers(string specId, IReadOnlyList<CloudSession> peers)
    {
        var openUnitIds = inbox.ListOpenFor(specId).Select(c => c.UnitId).Where(u => u is not null).ToHashSet();
        var units = new List<Unit>();
        foreach (var angle in ResearchAngles)
        {
            var peer = peers
                .Where(p => p.Tags.Contains("fyrd-role-researcher") && p.Tags.Contains("fyrd-unit-" + angle))
                .OrderByDescending(CreatedAt).FirstOrDefault();
            if (peer is null) continue;
            var st = openUnitIds.Contains(angle) ? UnitState.WaitingOnOwner
                : peer.SessionStatus == "idle" ? UnitState.Idle
                : UnitState.Running;
            units.Add(new Unit(angle, specId, angle, UnitStage.Research, st));
        }
        if (units.Count > 0) specs.WriteUnits(specId, units);
    }

    /// <summary>Research is over: mark its angle units Done so the board does not show them as running.</summary>
    private void CompleteResearchUnits(string specId)
    {
        var units = specs.ListUnits(specId);
        if (units.Count == 0) return;
        specs.WriteUnits(specId, units.Select(u => u with { Stage = UnitStage.Done, State = UnitState.Done }).ToList());
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

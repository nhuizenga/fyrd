using Fyrd.Core.CloudSessions;
using Fyrd.Core.Specs;

namespace Fyrd.Core.Orchestration;

/// <summary>
/// Relays the owner's inbox answers and squad-cap changes to the lead session, through the owner's CLI so the lead
/// reads them as the owner's.
/// </summary>
public sealed class LeadRelay(SpecStore specs, IOwnerMessages owner, RunnerState state, InboxStore inbox)
{
    private readonly HashSet<string> _inFlight = [];

    /// <summary>
    /// Sends the answer and records <c>sent</c>, or <c>failed</c> with the message. Returns the error message, or null on
    /// success. A card already being delivered (endpoint vs. retry pass) is skipped so it is sent once.
    /// </summary>
    public async Task<string?> DeliverAsync(InboxCard card, CancellationToken ct)
    {
        lock (_inFlight) if (!_inFlight.Add(card.Id)) return null;
        try
        {
            // The caller's snapshot may be stale: someone else may have delivered it since.
            try { card = inbox.Get(card.Id); }
            catch (KeyNotFoundException) { return null; }
            if (card.Answer?.RelayStatus == "sent") return null;
            if (await RelayAnswerAsync(card, ct)) inbox.SetRelay(card.Id, "sent", null);
            return null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            inbox.SetRelay(card.Id, "failed", e.Message);
            return e.Message;
        }
        finally
        {
            lock (_inFlight) _inFlight.Remove(card.Id);
        }
    }

    private const string AttentionPrefix = "attention:";

    /// <summary>Sends the answer to the spec's lead; false when there was nothing to send to.</summary>
    public async Task<bool> RelayAnswerAsync(InboxCard answered, CancellationToken ct)
    {
        if (answered.Answer is null || string.IsNullOrEmpty(answered.SourceKey)) return false;
        var sep = answered.SourceKey.IndexOf(':');
        if (sep <= 0) return false;
        var specId = answered.SourceKey[..sep];
        var rest = answered.SourceKey[(sep + 1)..];
        var sessionId = specs.Get(specId)?.SessionId;
        if (string.IsNullOrEmpty(sessionId) || specs.ListActive().All(r => r.Id != specId)) return false;

        var text = rest.StartsWith(AttentionPrefix, StringComparison.Ordinal)
            ? answered.Answer.Text
            : Protocol.FormatAnswer(rest, answered.Answer.Text);
        // The owner typed the answer, and leads honour a fyrd-answer only in an owner turn, never in a peer-wrapped one.
        await owner.SendAsync(sessionId, text, ct);
        state.ClearQuestionFlag(specId);
        return true;
    }

    public async Task RelayConfigAsync(int squadCap, CancellationToken ct)
    {
        var message = Protocol.FormatConfig(squadCap);
        foreach (var run in specs.ListActive())
            if (run.Status != SpecStatus.Researching && !string.IsNullOrEmpty(run.SessionId))
                await owner.SendAsync(run.SessionId, message, ct);
    }
}

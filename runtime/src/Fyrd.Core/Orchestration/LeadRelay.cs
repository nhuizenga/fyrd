using Fyrd.Core.CloudSessions;
using Fyrd.Core.Specs;

namespace Fyrd.Core.Orchestration;

/// <summary>Relays the owner's inbox answers and squad-cap changes to the lead session as user messages.</summary>
public sealed class LeadRelay(SpecStore specs, ICloudSessions cloud, RunnerState state, InboxStore inbox)
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
            await RelayAnswerAsync(card, ct);
            inbox.SetRelay(card.Id, "sent", null);
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

    public async Task RelayAnswerAsync(InboxCard answered, CancellationToken ct)
    {
        if (answered.Answer is null || string.IsNullOrEmpty(answered.SourceKey)) return;
        var sep = answered.SourceKey.IndexOf(':');
        if (sep <= 0) return;
        var specId = answered.SourceKey[..sep];
        var rest = answered.SourceKey[(sep + 1)..];
        var sessionId = specs.Get(specId)?.SessionId;
        if (string.IsNullOrEmpty(sessionId)) return;

        var text = rest.StartsWith(AttentionPrefix, StringComparison.Ordinal)
            ? answered.Answer.Text
            : Protocol.FormatAnswer(rest, answered.Answer.Text);
        await cloud.SendMessageAsync(sessionId, text, ct);
        state.ClearQuestionFlag(specId);
    }

    public async Task RelayConfigAsync(int squadCap, CancellationToken ct)
    {
        var message = Protocol.FormatConfig(squadCap);
        foreach (var run in specs.ListActive())
            if (!string.IsNullOrEmpty(run.SessionId))
                await cloud.SendMessageAsync(run.SessionId, message, ct);
    }
}

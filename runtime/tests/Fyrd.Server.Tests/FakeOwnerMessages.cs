using Fyrd.Core.CloudSessions;

namespace Fyrd.Server.Tests;

public sealed class FakeOwnerMessages : IOwnerMessages
{
    public List<(string SessionId, string Text)> Sent { get; } = [];
    public Exception? Fail { get; set; }

    public Task SendAsync(string sessionId, string text, CancellationToken ct = default)
    {
        if (Fail is not null) throw Fail;
        Sent.Add((sessionId, text));
        return Task.CompletedTask;
    }
}

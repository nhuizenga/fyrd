using Fyrd.Core.CloudSessions;

namespace Fyrd.Core.Tests;

public sealed class FakeOwnerMessages : IOwnerMessages
{
    private Exception? _next;

    public List<(string SessionId, string Text)> Sent { get; } = [];

    /// <summary>Every send throws this while it is set.</summary>
    public Exception? Fail { get; set; }

    /// <summary>The next send throws <paramref name="e"/>; later sends succeed.</summary>
    public void ThrowOnNextCall(Exception e) => _next = e;

    public Task SendAsync(string sessionId, string text, CancellationToken ct = default)
    {
        if (Fail is not null) throw Fail;
        if (_next is { } e) { _next = null; throw e; }
        Sent.Add((sessionId, text));
        return Task.CompletedTask;
    }
}

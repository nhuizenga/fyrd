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

    /// <summary>The ids the next creates return, in order; once empty, creates return <see cref="DefaultCreatedId"/>.</summary>
    public Queue<string> CreatedIds { get; } = new();

    public string DefaultCreatedId { get; set; } = "session_ctl";

    /// <summary>Every create, with its name and first turn (the first turn is also recorded in <see cref="Sent"/>).</summary>
    public List<(string SessionId, string Name, string Text)> Created { get; } = [];

    /// <summary>Runs on each create before it returns (for example to register the new session with a fake cloud).</summary>
    public Func<string, Task>? OnCreate { get; set; }

    public Task SendAsync(string sessionId, string text, CancellationToken ct = default)
    {
        if (Fail is not null) throw Fail;
        if (_next is { } e) { _next = null; throw e; }
        Sent.Add((sessionId, text));
        return Task.CompletedTask;
    }

    public async Task<string> CreateAsync(string name, string text, CancellationToken ct = default)
    {
        if (Fail is not null) throw Fail;
        if (_next is { } e) { _next = null; throw e; }
        var id = CreatedIds.Count > 0 ? CreatedIds.Dequeue() : DefaultCreatedId;
        if (OnCreate is not null) await OnCreate(id);
        Created.Add((id, name, text));
        Sent.Add((id, text));
        return id;
    }
}

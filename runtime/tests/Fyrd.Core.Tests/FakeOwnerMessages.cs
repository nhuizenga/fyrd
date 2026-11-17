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

    /// <summary>Every create, with its description and working directory.</summary>
    public List<(string SessionId, string Description, string WorkingDirectory)> Created { get; } = [];

    /// <summary>Every create throws this while it is set (sends are unaffected).</summary>
    public Exception? FailCreate { get; set; }

    /// <summary>Runs on each create before it returns (for example to register the new session with a fake cloud).</summary>
    public Func<string, Task>? OnCreate { get; set; }

    public Task SendAsync(string sessionId, string text, CancellationToken ct = default)
    {
        if (Fail is not null) throw Fail;
        if (_next is { } e) { _next = null; throw e; }
        Sent.Add((sessionId, text));
        return Task.CompletedTask;
    }

    public async Task<string> CreateAsync(string description, string workingDirectory, CancellationToken ct = default)
    {
        if (Fail is not null) throw Fail;
        if (FailCreate is not null) throw FailCreate;
        if (_next is { } e) { _next = null; throw e; }
        var id = CreatedIds.Count > 0 ? CreatedIds.Dequeue() : DefaultCreatedId;
        if (OnCreate is not null) await OnCreate(id);
        Created.Add((id, description, workingDirectory));
        return id;
    }
}

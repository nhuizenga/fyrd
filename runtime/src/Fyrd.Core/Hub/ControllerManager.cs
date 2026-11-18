using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fyrd.Core.CloudSessions;
using Fyrd.Core.Config;

namespace Fyrd.Core.Hub;

/// <summary>A Controller session. <paramref name="Warning"/> says what a create could not finish (archiving the previous
/// Controller) without failing it.</summary>
public sealed record ControllerInfo(string SessionId, string Url, string? Warning = null);

/// <summary>A Controller create is already running; a second one is refused rather than queued behind it.</summary>
public sealed class ControllerBusyException(string message) : InvalidOperationException(message);

/// <summary>The hub cannot act until the owner fixes a setting; the message says which.</summary>
public sealed class HubConfigException(string message) : InvalidOperationException(message);

/// <summary>
/// Creates, finds and retires the single Controller cloud session. Fyrd creates it through the owner's signed-in
/// <c>claude</c> CLI (<see cref="IOwnerMessages.CreateAsync"/>), so it is the owner's own session, then sends it the whole
/// opening prompt as an owner-authored turn. Its id is stored in <c>controller.json</c> under the hub dir.
/// </summary>
public sealed partial class ControllerManager
{
    public const string PromptPreamble =
        "You are the Fyrd Controller. I started this session for that role (its first message, \"controller\", only " +
        "named it), so please follow the instructions below.";

    /// <summary>What <c>claude --cloud</c> is given: the new session's title and its (otherwise ignored) first turn.</summary>
    public const string SessionDescription = "controller";

    /// <param name="PendingId">A session created for a new Controller whose opening prompt hasn't been delivered yet.</param>
    private sealed record State(string? SessionId, string? PostedMemoryHash, string? PostedInstructionsHash = null,
        string? PendingId = null);

    private readonly ICloudSessions _cloud;
    private readonly IOwnerMessages _owner;
    private readonly HubPersonas _personas;
    private readonly HubMemory _memory;
    private readonly ConfigStore _config;
    private readonly TimeProvider _time;
    private readonly HubSkills? _skills;
    private readonly string _file;
    private readonly SemaphoreSlim _gate = new(1);
    // Held for the whole CLI run, which can take minutes; _gate is only held for the short state and cloud steps around it,
    // so Open and Retire don't wait for the CLI.
    private readonly SemaphoreSlim _createGate = new(1);
    private readonly object _stateLock = new();
    private State _state;
    private DateTimeOffset? _createdAt;

    public ControllerManager(
        ICloudSessions cloud, IOwnerMessages owner, HubPersonas personas, HubMemory memory, ConfigStore config, FyrdPaths paths, TimeProvider? time = null, HubSkills? skills = null)
    {
        _skills = skills;
        _time = time ?? TimeProvider.System;
        _cloud = cloud;
        _owner = owner;
        _personas = personas;
        _memory = memory;
        _config = config;
        _file = Path.Combine(paths.HubDir, "controller.json");
        LaunchDirectory = Path.Combine(paths.HubDir, "controller-launch");
        _state = LoadState();
    }

    public string? CurrentId { get { lock (_stateLock) return _state.SessionId; } }

    /// <summary>A created session still waiting for its opening prompt (see <see cref="CreateAsync"/>).</summary>
    public string? PendingId { get { lock (_stateLock) return _state.PendingId; } }

    /// <summary>The folder <c>claude --cloud</c> runs in: Fyrd's own, not a git repository, so the session starts empty,
    /// and stable, so the owner only has to trust it once if the CLI asks.</summary>
    public string LaunchDirectory { get; }

    /// <summary>When this process last created a Controller (in memory only). Until a poll that ran later has listed it,
    /// the console still reports the stored id.</summary>
    public DateTimeOffset? CreatedAt { get { lock (_stateLock) return _createdAt; } }

    public string? PostedMemoryHash
    {
        get { lock (_stateLock) return _state.PostedMemoryHash; }
        set { lock (_stateLock) SaveState(_state with { PostedMemoryHash = value }); }
    }

    public string? PostedInstructionsHash
    {
        get { lock (_stateLock) return _state.PostedInstructionsHash; }
        set { lock (_stateLock) SaveState(_state with { PostedInstructionsHash = value }); }
    }

    /// <summary>True when skills are configured but none were found (a missing or wrong directory). The runner then skips
    /// the refresh rather than strip every skill from the live Controller.</summary>
    public bool SkillsMissing => _skills is not null && _skills.List().Count == 0;

    /// <summary>Records what was just posted to the Controller, in one write.</summary>
    public void MarkPosted(string instructionsHash, string memoryHash)
    {
        lock (_stateLock) SaveState(_state with { PostedInstructionsHash = instructionsHash, PostedMemoryHash = memoryHash });
    }

    /// <summary>
    /// The full message that opens a new Controller or refreshes its instructions (preamble, instructions, memory), plus the
    /// hashes of its instructions part and of its memory part. All three come from one snapshot, so what is marked as
    /// posted is exactly what is sent.
    /// </summary>
    public (string Prompt, string InstructionsHash, string MemoryHash) BuildPrompt()
    {
        var personas = _personas.List();
        var config = _config.Load();
        var skills = _skills?.List();
        return (PromptPreamble + "\n\n" + HubPrompt.Controller(personas, _memory, config, skills),
            Hash(HubPrompt.Instructions(personas, config, skills)),
            MemoryHash(_memory, personas));
    }

    /// <summary>Lowercase hex SHA-256 of the instructions part of the prompt (no memory, which has its own hash).</summary>
    public string InstructionsHash() => BuildPrompt().InstructionsHash;

    /// <summary>Lowercase hex SHA-256 of the memory-update text for the current memory; the poll pass compares it to <see cref="PostedMemoryHash"/>.</summary>
    public static string MemoryHash(HubMemory memory, IReadOnlyList<HubPersona> personas) =>
        Hash(HubPrompt.MemoryUpdate(memory, personas));

    /// <summary>Lowercase hex SHA-256 of the UTF-8 text.</summary>
    public static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>
    /// Creates a new Controller session as the owner and sends it the whole Controller prompt, then stores it as the
    /// Controller and archives a different live previous one on a best-effort basis.
    /// <list type="number">
    /// <item><c>claude --cloud controller</c> creates the session (its first turn is just "controller"). Its id is stored
    /// as pending at once, so a session the CLI created is never orphaned.</item>
    /// <item>The prompt is sent with <see cref="IOwnerMessages.SendAsync"/>. If that fails, the pending session is kept and
    /// the next create re-sends the prompt to it instead of creating another session.</item>
    /// <item>Only then does the session replace the current Controller, which is left alone until this point.</item>
    /// </list>
    /// A second create while one runs throws <see cref="ControllerBusyException"/>.
    /// </summary>
    public async Task<ControllerInfo> CreateAsync(CancellationToken ct)
    {
        if (!await _createGate.WaitAsync(0, ct))
            throw new ControllerBusyException("A Controller is already being created.");
        try
        {
            // Built before any side effect, so a missing Controller persona leaves the current Controller alone.
            var (prompt, instructionsHash, hash) = BuildPrompt();

            string id;
            if (PendingId is { } pending && await LiveAsync(pending, ct) is not null)
            {
                id = pending;
            }
            else
            {
                id = await _owner.CreateAsync(SessionDescription, LaunchDirectory, ct);
                if (!SessionIdRegex().IsMatch(id))
                    throw new HubConfigException($"The claude CLI returned an unexpected session id ({id}).");
                lock (_stateLock) SaveState(_state with { PendingId = id });
            }

            // The opening prompt is Fyrd's own text, but the session only honours it as the owner's turn, which the CLI sends.
            try { await _owner.SendAsync(id, prompt, CancellationToken.None); }
            catch (OwnerMessageException e)
            {
                throw new OwnerMessageException(
                    $"The Controller session was created ({id}), but its opening prompt wasn't delivered: {e.Message.TrimEnd().TrimEnd('.')}. " +
                    "Create the Controller again to resend it to the same session.");
            }

            // From here the new session is the Controller: store it, and don't let the request's cancellation stop that.
            await _gate.WaitAsync(CancellationToken.None);
            try
            {
                string? previous;
                lock (_stateLock)
                {
                    previous = _state.SessionId;
                    SaveState(new State(id, hash, instructionsHash));
                    _createdAt = _time.GetUtcNow();
                }
                var warning = previous is not null && previous != id ? await TryArchiveAsync(previous) : null;
                return Info(id) with { Warning = warning };
            }
            finally { _gate.Release(); }
        }
        finally { _createGate.Release(); }
    }

    /// <summary>Archives a previous Controller if it is still live; returns a warning instead of throwing when that fails.</summary>
    private async Task<string?> TryArchiveAsync(string previous)
    {
        try
        {
            if (await LiveAsync(previous, CancellationToken.None) is not null) await _cloud.ArchiveAsync(previous, CancellationToken.None);
            return null;
        }
        catch (CloudSessionApiException e) when (e.Status == 404) { return null; }
        catch (Exception e) when (e is CloudSessionApiException or HttpRequestException or CloudSessionAuthException
                                      or ClaudeAuthMissingException or TaskCanceledException)
        {
            return $"The new Controller was created, but the previous one ({previous}) could not be archived: {e.Message} " +
                   "Archive it on claude.ai/code.";
        }
    }

    /// <summary>The stored Controller when it is still live; otherwise clears the stored id and returns null.
    /// Errors other than 404 propagate and change nothing.</summary>
    public async Task<ControllerInfo?> FindAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (CurrentId is not { } stored) return null;
            if (await LiveAsync(stored, ct) is not null) return Info(stored);
            lock (_stateLock) SaveState(new State(null, null, PendingId: _state.PendingId));
            return null;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Archives the current Controller (404 ignored) and clears the stored id; the owner then creates a new one. A pending
    /// session (created, but its prompt not delivered) is archived and cleared too, on a best-effort basis, so the next
    /// create starts a fresh conversation rather than reusing it; it is left alone while a create is running.
    /// </summary>
    public async Task RetireAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var current = CurrentId;
            var pending = _createGate.CurrentCount > 0 ? PendingId : null;
            if (current is null && pending is null) return;
            if (current is not null)
            {
                try { await _cloud.ArchiveAsync(current, ct); }
                catch (CloudSessionApiException e) when (e.Status == 404) { }
            }
            if (pending is not null && pending != current)
            {
                try { await _cloud.ArchiveAsync(pending, ct); }
                catch (Exception e) when (e is CloudSessionApiException or HttpRequestException or CloudSessionAuthException
                                              or ClaudeAuthMissingException) { }
            }
            lock (_stateLock) SaveState(new State(null, null, PendingId: pending is null ? _state.PendingId : null));
        }
        finally { _gate.Release(); }
    }

    /// <summary>The session, or null when it is archived or gone (404); other errors propagate.</summary>
    private async Task<CloudSession?> LiveAsync(string id, CancellationToken ct)
    {
        try
        {
            var s = await _cloud.GetAsync(id, ct);
            return s.SessionStatus == "archived" ? null : s;
        }
        catch (CloudSessionApiException e) when (e.Status == 404) { return null; }
    }

    [GeneratedRegex("^session_[A-Za-z0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SessionIdRegex();

    private static ControllerInfo Info(string id) => new(id, "https://claude.ai/code/" + id);

    private State LoadState()
    {
        try
        {
            if (File.Exists(_file))
                return JsonSerializer.Deserialize<State>(File.ReadAllText(_file), Json.Options) ?? new State(null, null);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { }
        return new State(null, null);
    }

    private void SaveState(State s)
    {
        AtomicFile.Write(_file, JsonSerializer.Serialize(s, Json.Options));
        _state = s;
    }
}

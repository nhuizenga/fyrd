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
/// <c>claude</c> CLI (<see cref="IOwnerMessages.CreateAsync"/>), so it is the owner's own session and the whole opening
/// prompt is its first, owner-authored turn. Its id is stored in <c>controller.json</c> under the hub dir.
/// </summary>
public sealed partial class ControllerManager
{
    public const string PromptPreamble =
        "You are the Fyrd Controller. I started this session for that role, so please follow the instructions below.";

    /// <summary>The name the Controller session is created with.</summary>
    public const string SessionName = "Fyrd Controller";

    private sealed record State(string? SessionId, string? PostedMemoryHash, string? PostedInstructionsHash = null);

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
        _state = LoadState();
    }

    public string? CurrentId { get { lock (_stateLock) return _state.SessionId; } }

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
    /// Creates a new Controller session as the owner with the whole Controller prompt as its first turn, stores the new id,
    /// then archives a different live previous Controller on a best-effort basis. A create that fails leaves the current
    /// Controller alone; once the CLI has created the session, its id is always stored, so it is never orphaned. A second
    /// create while one runs throws <see cref="ControllerBusyException"/>.
    /// </summary>
    public async Task<ControllerInfo> CreateAsync(CancellationToken ct)
    {
        if (!await _createGate.WaitAsync(0, ct))
            throw new ControllerBusyException("A Controller is already being created.");
        try
        {
            // Built before any side effect, so a missing Controller persona leaves the current Controller alone.
            var (prompt, instructionsHash, hash) = BuildPrompt();

            // The opening prompt is Fyrd's own text, but the session only honours it as the owner's turn: the CLI creates
            // the session as the owner and sends the prompt as that turn, in full.
            var id = await _owner.CreateAsync(SessionName, prompt, ct);
            if (!SessionIdRegex().IsMatch(id))
                throw new HubConfigException($"The claude CLI returned an unexpected session id ({id}).");

            // From here the new session exists: store it first, and don't let the request's cancellation stop that.
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
            lock (_stateLock) SaveState(new State(null, null));
            return null;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Archives the current Controller (404 ignored) and clears the stored id; the owner then creates a new one.</summary>
    public async Task RetireAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (CurrentId is not { } current) return;
            try { await _cloud.ArchiveAsync(current, ct); }
            catch (CloudSessionApiException e) when (e.Status == 404) { }
            lock (_stateLock) SaveState(new State(null, null));
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

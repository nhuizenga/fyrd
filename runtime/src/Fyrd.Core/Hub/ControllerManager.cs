using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fyrd.Core.CloudSessions;
using Fyrd.Core.Config;

namespace Fyrd.Core.Hub;

public sealed record ControllerInfo(string SessionId, string Url);

/// <summary>The hub cannot act until the owner fixes a setting; the message says which.</summary>
public sealed class HubConfigException(string message) : InvalidOperationException(message);

/// <summary>
/// Adopts, finds and retires the single Controller cloud session. The owner creates it on claude.ai; Fyrd never creates it.
/// Its id is stored in <c>controller.json</c> under the hub dir.
/// </summary>
public sealed partial class ControllerManager
{
    public const string PromptPreamble =
        "You are the Fyrd Controller. Follow the instructions below for the rest of this session; they replace any earlier instructions.";
    private const string Unavailable = "That session isn't available. Create a new one on claude.ai and paste its id.";
    private const string Peasant = "That's a Fyrd peasant session. Create a fresh session on claude.ai and paste its id.";

    private sealed record State(string? SessionId, string? PostedMemoryHash);

    private readonly ICloudSessions _cloud;
    private readonly HubPersonas _personas;
    private readonly HubMemory _memory;
    private readonly ConfigStore _config;
    private readonly TimeProvider _time;
    private readonly string _file;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly object _stateLock = new();
    private State _state;
    private DateTimeOffset? _adoptedAt;

    public ControllerManager(
        ICloudSessions cloud, HubPersonas personas, HubMemory memory, ConfigStore config, FyrdPaths paths, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _cloud = cloud;
        _personas = personas;
        _memory = memory;
        _config = config;
        _file = Path.Combine(paths.HubDir, "controller.json");
        _state = LoadState();
    }

    public string? CurrentId { get { lock (_stateLock) return _state.SessionId; } }

    /// <summary>When this process last adopted a Controller (in memory only). Until a poll that ran later has listed it,
    /// the console still reports the stored id.</summary>
    public DateTimeOffset? AdoptedAt { get { lock (_stateLock) return _adoptedAt; } }

    public string? PostedMemoryHash
    {
        get { lock (_stateLock) return _state.PostedMemoryHash; }
        set { lock (_stateLock) SaveState(_state with { PostedMemoryHash = value }); }
    }

    /// <summary>Lowercase hex SHA-256 of the memory-update text for the current memory; the poll pass compares it to <see cref="PostedMemoryHash"/>.</summary>
    public static string MemoryHash(HubMemory memory, IReadOnlyList<HubPersona> personas) =>
        Hash(HubPrompt.MemoryUpdate(memory, personas));

    /// <summary>Lowercase hex SHA-256 of the UTF-8 text.</summary>
    public static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>
    /// Adopts a session the owner created on claude.ai (Fyrd-created sessions get no claude.ai connectors, so they
    /// cannot create sessions): checks it is live, archives a different live previous Controller, sends the Controller
    /// prompt as a user message, and stores the id.
    /// </summary>
    public async Task<ControllerInfo> AdoptAsync(string sessionId, CancellationToken ct)
    {
        var id = (sessionId ?? "").Trim();
        if (!SessionIdRegex().IsMatch(id))
            throw new HubConfigException("That isn't a session id. Paste the id that starts with session_ (the last part of the session's claude.ai URL).");

        await _gate.WaitAsync(ct);
        try
        {
            var session = await LiveAsync(id, ct) ?? throw new HubConfigException(Unavailable);
            // Peasants carry Fyrd tags and were created through the API, so they lack connectors; owner-made sessions have none.
            if (session.Tags.Any(t => t == "fyrd-hub" || t.StartsWith("fyrd-role-", StringComparison.Ordinal)))
                throw new HubConfigException(Peasant);

            // Built before any side effect, so a missing Controller persona leaves the current Controller alone.
            var personas = _personas.List();
            var hash = MemoryHash(_memory, personas);
            var prompt = PromptPreamble + "\n\n" + HubPrompt.Controller(personas, _memory, _config.Load());

            if (CurrentId is { } previous && previous != id && await LiveAsync(previous, ct) is not null)
            {
                try { await _cloud.ArchiveAsync(previous, ct); }
                catch (CloudSessionApiException e) when (e.Status == 404) { }
            }

            await _cloud.SendMessageAsync(id, prompt, ct);
            lock (_stateLock)
            {
                SaveState(new State(id, hash));
                _adoptedAt = _time.GetUtcNow();
            }
            return Info(id);
        }
        finally { _gate.Release(); }
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

    /// <summary>Archives the current Controller (404 ignored) and clears the stored id; the owner then adopts a new one.</summary>
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

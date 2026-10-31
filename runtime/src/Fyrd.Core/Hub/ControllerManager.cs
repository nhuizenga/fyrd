using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fyrd.Core.CloudSessions;
using Fyrd.Core.Config;

namespace Fyrd.Core.Hub;

public sealed record ControllerInfo(string SessionId, string Url);

/// <summary>Finds, creates and recreates the single Controller cloud session; its id is stored in <c>controller.json</c> under the hub dir.</summary>
public sealed class ControllerManager
{
    private const string ControllerTag = "fyrd-controller";
    private const string FirstMessage = "You are online. Wait for the owner's first request.";

    private sealed record State(string? SessionId, string? PostedMemoryHash);

    private readonly ICloudSessions _cloud;
    private readonly HubPersonas _personas;
    private readonly HubMemory _memory;
    private readonly ConfigStore _config;
    private readonly string _file;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly object _stateLock = new();
    private State _state;

    public ControllerManager(ICloudSessions cloud, HubPersonas personas, HubMemory memory, ConfigStore config, FyrdPaths paths)
    {
        _cloud = cloud;
        _personas = personas;
        _memory = memory;
        _config = config;
        _file = Path.Combine(paths.HubDir, "controller.json");
        _state = LoadState();
    }

    public string? CurrentId { get { lock (_stateLock) return _state.SessionId; } }

    public string? PostedMemoryHash
    {
        get { lock (_stateLock) return _state.PostedMemoryHash; }
        set { lock (_stateLock) SaveState(_state with { PostedMemoryHash = value }); }
    }

    /// <summary>Lowercase hex SHA-256 of the memory-update text for the current memory; the poll pass compares it to <see cref="PostedMemoryHash"/>.</summary>
    public static string MemoryHash(HubMemory memory, IReadOnlyList<HubPersona> personas) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(HubPrompt.MemoryUpdate(memory, personas)))).ToLowerInvariant();

    public async Task<ControllerInfo> FindOrCreateAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (CurrentId is { } stored)
            {
                try
                {
                    var s = await _cloud.GetAsync(stored, ct);
                    if (s.SessionStatus != "archived") return Info(stored);
                }
                catch (CloudSessionApiException e) when (e.Status == 404) { }
            }

            var live = (await _cloud.ListByTagAsync(ControllerTag, ct))
                .Where(s => s.SessionStatus != "archived")
                .OrderByDescending(CreatedAt)
                .ToList();
            if (live.Count > 0)
            {
                foreach (var extra in live.Skip(1))
                {
                    try { await _cloud.ArchiveAsync(extra.Id, ct); }
                    catch (CloudSessionApiException e) when (e.Status == 404) { }
                }
                lock (_stateLock) SaveState(new State(live[0].Id, null));
                return Info(live[0].Id);
            }
            return await CreateAsync(ct);
        }
        finally { _gate.Release(); }
    }

    public async Task<ControllerInfo> RecreateAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (CurrentId is { } current)
            {
                try { await _cloud.ArchiveAsync(current, ct); }
                catch (CloudSessionApiException e) when (e.Status == 404) { }
                lock (_stateLock) SaveState(new State(null, null));
            }
            return await CreateAsync(ct);
        }
        finally { _gate.Release(); }
    }

    private async Task<ControllerInfo> CreateAsync(CancellationToken ct)
    {
        var config = _config.Load();
        if (config.EnvironmentId is null) throw new InvalidOperationException("Set a cloud environment in Settings first.");
        var personas = _personas.List();
        var controller = personas.FirstOrDefault(p => p.Id == "controller")
            ?? throw new InvalidOperationException("The Controller persona is missing.");
        var hash = MemoryHash(_memory, personas);
        var created = await _cloud.CreateAsync(new CreateSessionRequest(
            "controller",
            ["fyrd-hub", "fyrd-role-controller", ControllerTag],
            config.EnvironmentId,
            FirstMessage,
            controller.Model,
            HubPrompt.Controller(personas, _memory, config),
            null,
            "auto"), ct);
        lock (_stateLock) SaveState(new State(created.Id, hash));
        return Info(created.Id);
    }

    private static ControllerInfo Info(string id) => new(id, "https://claude.ai/code/" + id);

    private static DateTimeOffset CreatedAt(CloudSession s) =>
        s.Raw.ValueKind == JsonValueKind.Object
        && s.Raw.TryGetProperty("created_at", out var v)
        && v.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(v.GetString(), out var at)
            ? at
            : DateTimeOffset.MinValue;

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

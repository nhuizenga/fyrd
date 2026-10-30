using System.Text.Json;

namespace Fyrd.Core.Config;

public sealed partial record FyrdConfig(
    int SquadCap = 4,
    string? EnvironmentId = null,
    string LeadModel = FyrdConfig.DefaultLeadModel,
    string PeerModel = FyrdConfig.DefaultPeerModel)
{
    public const int MinSquadCap = 1, MaxSquadCap = 8;
    public const string DefaultLeadModel = "claude-opus-5-5", DefaultPeerModel = "claude-sonnet-5-5";

    public static bool IsValidEnvironmentId(string? id) => id is not null && EnvironmentIdPattern().IsMatch(id);

    [System.Text.RegularExpressions.GeneratedRegex(@"^(env|ccpool)_[A-Za-z0-9]+\z")]
    private static partial System.Text.RegularExpressions.Regex EnvironmentIdPattern();
}

public sealed class ConfigStore(FyrdPaths paths)
{
    public FyrdConfig Load()
    {
        try
        {
            if (!File.Exists(paths.ConfigFile)) return new FyrdConfig();
            var config = JsonSerializer.Deserialize<FyrdConfig>(File.ReadAllText(paths.ConfigFile), Json.Options)
                ?? new FyrdConfig();
            return config with
            {
                SquadCap = Math.Clamp(config.SquadCap, FyrdConfig.MinSquadCap, FyrdConfig.MaxSquadCap),
                EnvironmentId = FyrdConfig.IsValidEnvironmentId(config.EnvironmentId) ? config.EnvironmentId : null,
                LeadModel = string.IsNullOrWhiteSpace(config.LeadModel) ? FyrdConfig.DefaultLeadModel : config.LeadModel,
                PeerModel = string.IsNullOrWhiteSpace(config.PeerModel) ? FyrdConfig.DefaultPeerModel : config.PeerModel,
            };
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return new FyrdConfig();
        }
    }

    public void Save(FyrdConfig config)
    {
        if (config.SquadCap is < FyrdConfig.MinSquadCap or > FyrdConfig.MaxSquadCap)
            throw new ArgumentOutOfRangeException(nameof(config), config.SquadCap,
                $"SquadCap must be between {FyrdConfig.MinSquadCap} and {FyrdConfig.MaxSquadCap}.");
        if (config.EnvironmentId is not null && !FyrdConfig.IsValidEnvironmentId(config.EnvironmentId))
            throw new ArgumentException("EnvironmentId must match ^(env|ccpool)_[A-Za-z0-9]+$.", nameof(config));
        AtomicFile.Write(paths.ConfigFile, JsonSerializer.Serialize(config, Json.Options));
    }
}

using System.Text.Json;

namespace Fyrd.Core.Config;

public sealed record FyrdConfig(int SquadCap = 4)
{
    public const int MinSquadCap = 1, MaxSquadCap = 8;
}

public sealed class ConfigStore(FyrdPaths paths)
{
    public FyrdConfig Load() =>
        File.Exists(paths.ConfigFile)
            ? JsonSerializer.Deserialize<FyrdConfig>(File.ReadAllText(paths.ConfigFile), Json.Options) ?? new FyrdConfig()
            : new FyrdConfig();

    public void Save(FyrdConfig config)
    {
        if (config.SquadCap is < FyrdConfig.MinSquadCap or > FyrdConfig.MaxSquadCap)
            throw new ArgumentOutOfRangeException(nameof(config), config.SquadCap,
                $"SquadCap must be between {FyrdConfig.MinSquadCap} and {FyrdConfig.MaxSquadCap}.");
        File.WriteAllText(paths.ConfigFile, JsonSerializer.Serialize(config, Json.Options));
    }
}

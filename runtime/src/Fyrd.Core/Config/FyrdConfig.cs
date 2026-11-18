using System.Text.Json;

namespace Fyrd.Core.Config;

public sealed partial record FyrdConfig(
    string? EnvironmentId = null,
    int SessionCap = 20,
    decimal SessionDailyLimitUsd = FyrdConfig.DefaultSessionDailyLimitUsd,
    decimal FyrdDailyLimitUsd = FyrdConfig.DefaultFyrdDailyLimitUsd,
    string? TimeZone = null)
{
    public const decimal MinDailyLimitUsd = 1, MaxDailyLimitUsd = 10_000;
    public const decimal DefaultSessionDailyLimitUsd = 50, DefaultFyrdDailyLimitUsd = 500;

    public const int MinSessionCap = 1, MaxSessionCap = 100;

    /// <summary>Resolves an IANA (or platform) time-zone id; null, empty or unknown ids give null.</summary>
    public static TimeZoneInfo? FindTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { return null; }
    }

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
                EnvironmentId = FyrdConfig.IsValidEnvironmentId(config.EnvironmentId) ? config.EnvironmentId : null,
                SessionCap = Math.Clamp(config.SessionCap, FyrdConfig.MinSessionCap, FyrdConfig.MaxSessionCap),
                SessionDailyLimitUsd = Math.Clamp(config.SessionDailyLimitUsd, FyrdConfig.MinDailyLimitUsd, FyrdConfig.MaxDailyLimitUsd),
                FyrdDailyLimitUsd = Math.Clamp(config.FyrdDailyLimitUsd, FyrdConfig.MinDailyLimitUsd, FyrdConfig.MaxDailyLimitUsd),
                TimeZone = FyrdConfig.FindTimeZone(config.TimeZone) is null ? null : config.TimeZone,
            };
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return new FyrdConfig();
        }
    }

    public void Save(FyrdConfig config)
    {
        if (config.SessionCap is < FyrdConfig.MinSessionCap or > FyrdConfig.MaxSessionCap)
            throw new ArgumentOutOfRangeException(nameof(config), config.SessionCap,
                $"SessionCap must be between {FyrdConfig.MinSessionCap} and {FyrdConfig.MaxSessionCap}.");
        if (config.SessionDailyLimitUsd is < FyrdConfig.MinDailyLimitUsd or > FyrdConfig.MaxDailyLimitUsd)
            throw new ArgumentOutOfRangeException(nameof(config), config.SessionDailyLimitUsd,
                $"SessionDailyLimitUsd must be between {FyrdConfig.MinDailyLimitUsd} and {FyrdConfig.MaxDailyLimitUsd}.");
        if (config.FyrdDailyLimitUsd is < FyrdConfig.MinDailyLimitUsd or > FyrdConfig.MaxDailyLimitUsd)
            throw new ArgumentOutOfRangeException(nameof(config), config.FyrdDailyLimitUsd,
                $"FyrdDailyLimitUsd must be between {FyrdConfig.MinDailyLimitUsd} and {FyrdConfig.MaxDailyLimitUsd}.");
        if (config.TimeZone is not null && FyrdConfig.FindTimeZone(config.TimeZone) is null)
            throw new ArgumentException("TimeZone must be a known IANA time-zone id.", nameof(config));
        if (config.EnvironmentId is not null && !FyrdConfig.IsValidEnvironmentId(config.EnvironmentId))
            throw new ArgumentException("EnvironmentId must match ^(env|ccpool)_[A-Za-z0-9]+$.", nameof(config));
        AtomicFile.Write(paths.ConfigFile, JsonSerializer.Serialize(config, Json.Options));
    }
}

using Fyrd.Core;
using Fyrd.Core.Config;

namespace Fyrd.Core.Tests;

public class ConfigStoreTests
{
    [Fact] public void Load_MissingFile_ReturnsDefaultCap4()
    { using var h = new TempHome(); Assert.Equal(4, new ConfigStore(h.Paths).Load().SquadCap); }

    [Fact] public void Save_ThenLoad_RoundTrips()
    { using var h = new TempHome(); var s = new ConfigStore(h.Paths);
      s.Save(new FyrdConfig(6)); Assert.Equal(6, s.Load().SquadCap);
      Assert.Contains("\"squadCap\": 6", File.ReadAllText(h.Paths.ConfigFile)); }

    [Theory, InlineData(0), InlineData(9)]
    public void Save_OutOfRange_Throws(int cap)
    { using var h = new TempHome();
      Assert.Throws<ArgumentOutOfRangeException>(() => new ConfigStore(h.Paths).Save(new FyrdConfig(cap))); }

    [Fact] public void Load_MalformedFile_ReturnsDefaults()
    { using var h = new TempHome(); File.WriteAllText(h.Paths.ConfigFile, "{ nope");
      Assert.Equal(4, new ConfigStore(h.Paths).Load().SquadCap); }

    [Theory, InlineData(0, 1), InlineData(50, 8)]
    public void Load_OutOfRangeCap_Clamps(int raw, int expected)
    { using var h = new TempHome(); File.WriteAllText(h.Paths.ConfigFile, $"{{\"squadCap\": {raw}}}");
      Assert.Equal(expected, new ConfigStore(h.Paths).Load().SquadCap); }

    [Fact] public void Resolve_PrefersConfiguredOverEnv()
    { using var h = new TempHome(); Assert.Equal(h.Paths.Root, FyrdPaths.Resolve(h.Paths.Root).Root); }

    [Fact] public void Config_DefaultsModelsAndNullEnvironment()
    { using var h = new TempHome(); var c = new ConfigStore(h.Paths).Load();
      Assert.Null(c.EnvironmentId); Assert.Equal("claude-opus-5-5", c.LeadModel); Assert.Equal("claude-sonnet-5-5", c.PeerModel); }

    [Fact] public void Config_Save_RejectsBadEnvironmentId()
    { using var h = new TempHome();
      Assert.Throws<ArgumentException>(() => new ConfigStore(h.Paths).Save(new FyrdConfig(4, "env;rm"))); }

    [Fact] public void Config_Save_AcceptsValidEnvironmentId()
    { using var h = new TempHome(); var s = new ConfigStore(h.Paths);
      s.Save(new FyrdConfig(4, "env_01Abc")); Assert.Equal("env_01Abc", s.Load().EnvironmentId); }

    [Fact] public void Load_Default_SessionCapIs20()
    { using var home = new TempHome(); Assert.Equal(20, new ConfigStore(home.Paths).Load().SessionCap); }

    [Fact] public void Load_OutOfRangeSessionCap_Clamped()
    { using var home = new TempHome(); File.WriteAllText(home.Paths.ConfigFile, "{\"sessionCap\":500}");
      Assert.Equal(100, new ConfigStore(home.Paths).Load().SessionCap); }

    [Fact] public void Save_SessionCapZero_Throws()
    { using var home = new TempHome();
      Assert.Throws<ArgumentOutOfRangeException>(() => new ConfigStore(home.Paths).Save(new FyrdConfig(SessionCap: 0))); }

    [Fact] public void Paths_CreateHubDirs()
    { using var home = new TempHome();
      Assert.True(Directory.Exists(home.Paths.HubDir));
      Assert.True(Directory.Exists(home.Paths.HubPersonasDir));
      Assert.True(Directory.Exists(home.Paths.HubMemoryDir)); }

    [Fact] public void Config_Load_EmptyModelFallsBack()
    { using var h = new TempHome(); File.WriteAllText(h.Paths.ConfigFile, "{\"leadModel\": \"\", \"peerModel\": null}");
      var c = new ConfigStore(h.Paths).Load();
      Assert.Equal("claude-opus-5-5", c.LeadModel); Assert.Equal("claude-sonnet-5-5", c.PeerModel); }

    [Fact] public void Load_DailyLimitDefaults_50And500()
    { using var home = new TempHome(); var c = new ConfigStore(home.Paths).Load();
      Assert.Equal(50m, c.SessionDailyLimitUsd); Assert.Equal(500m, c.FyrdDailyLimitUsd); Assert.Null(c.TimeZone); }

    [Fact] public void Load_OutOfRangeDailyLimits_Clamped()
    { using var home = new TempHome(); File.WriteAllText(home.Paths.ConfigFile, "{\"sessionDailyLimitUsd\":0,\"fyrdDailyLimitUsd\":99999}");
      var c = new ConfigStore(home.Paths).Load(); Assert.Equal(1m, c.SessionDailyLimitUsd); Assert.Equal(10000m, c.FyrdDailyLimitUsd); }

    [Theory] [InlineData(0.5)] [InlineData(10001)]
    public void Save_SessionDailyLimitOutOfRange_Throws(double v)
    { using var home = new TempHome();
      Assert.Throws<ArgumentOutOfRangeException>(() => new ConfigStore(home.Paths).Save(new FyrdConfig(SessionDailyLimitUsd: (decimal)v))); }

    [Theory] [InlineData(0)] [InlineData(10001)]
    public void Save_FyrdDailyLimitOutOfRange_Throws(double v)
    { using var home = new TempHome();
      Assert.Throws<ArgumentOutOfRangeException>(() => new ConfigStore(home.Paths).Save(new FyrdConfig(FyrdDailyLimitUsd: (decimal)v))); }

    [Fact] public void Save_UnknownTimeZone_Throws()
    { using var home = new TempHome();
      Assert.Throws<ArgumentException>(() => new ConfigStore(home.Paths).Save(new FyrdConfig(TimeZone: "Mars/Olympus"))); }

    [Fact] public void Save_KnownTimeZone_RoundTrips()
    { using var home = new TempHome(); var s = new ConfigStore(home.Paths);
      s.Save(new FyrdConfig(TimeZone: "America/Los_Angeles", SessionDailyLimitUsd: 12.5m));
      var c = s.Load(); Assert.Equal("America/Los_Angeles", c.TimeZone); Assert.Equal(12.5m, c.SessionDailyLimitUsd); }

    [Fact] public void Load_UnknownTimeZone_Ignored()
    { using var home = new TempHome(); File.WriteAllText(home.Paths.ConfigFile, "{\"timeZone\":\"Mars/Olympus\"}");
      Assert.Null(new ConfigStore(home.Paths).Load().TimeZone); }
}

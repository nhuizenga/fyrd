using Fyrd.Core;
using Fyrd.Core.Config;

namespace Fyrd.Core.Tests;

public class ConfigStoreTests
{
    [Fact] public void Save_ThenLoad_RoundTrips()
    { using var h = new TempHome(); var s = new ConfigStore(h.Paths);
      s.Save(new FyrdConfig(SessionCap: 6)); Assert.Equal(6, s.Load().SessionCap);
      Assert.Contains("\"sessionCap\": 6", File.ReadAllText(h.Paths.ConfigFile)); }

    [Theory, InlineData(0), InlineData(101)]
    public void Save_OutOfRange_Throws(int cap)
    { using var h = new TempHome();
      Assert.Throws<ArgumentOutOfRangeException>(() => new ConfigStore(h.Paths).Save(new FyrdConfig(SessionCap: cap))); }

    [Fact] public void Load_MalformedFile_ReturnsDefaults()
    { using var h = new TempHome(); File.WriteAllText(h.Paths.ConfigFile, "{ nope");
      Assert.Equal(20, new ConfigStore(h.Paths).Load().SessionCap); }

    [Theory, InlineData(0, 1), InlineData(500, 100)]
    public void Load_OutOfRangeCap_Clamps(int raw, int expected)
    { using var h = new TempHome(); File.WriteAllText(h.Paths.ConfigFile, $"{{\"sessionCap\": {raw}}}");
      Assert.Equal(expected, new ConfigStore(h.Paths).Load().SessionCap); }

    [Fact] public void Resolve_PrefersConfiguredOverEnv()
    { using var h = new TempHome(); Assert.Equal(h.Paths.Root, FyrdPaths.Resolve(h.Paths.Root).Root); }

    [Fact] public void Config_DefaultsNullEnvironment()
    { using var h = new TempHome(); Assert.Null(new ConfigStore(h.Paths).Load().EnvironmentId); }

    [Fact] public void Config_Save_RejectsBadEnvironmentId()
    { using var h = new TempHome();
      Assert.Throws<ArgumentException>(() => new ConfigStore(h.Paths).Save(new FyrdConfig("env;rm"))); }

    [Fact] public void Config_Save_AcceptsValidEnvironmentId()
    { using var h = new TempHome(); var s = new ConfigStore(h.Paths);
      s.Save(new FyrdConfig("env_01Abc")); Assert.Equal("env_01Abc", s.Load().EnvironmentId); }

    [Fact] public void Load_Default_SessionCapIs20()
    { using var home = new TempHome(); Assert.Equal(20, new ConfigStore(home.Paths).Load().SessionCap); }

    [Fact] public void Paths_CreateHubDirs()
    { using var home = new TempHome();
      Assert.True(Directory.Exists(home.Paths.HubDir));
      Assert.True(Directory.Exists(home.Paths.HubPersonasDir));
      Assert.True(Directory.Exists(home.Paths.HubMemoryDir)); }

    [Fact] public void Load_FileWithFieldsNoLongerUsed_KeepsTheRest()
    { using var h = new TempHome(); File.WriteAllText(h.Paths.ConfigFile, "{\"squadCap\": 6, \"leadModel\": \"m\", \"sessionCap\": 7}");
      Assert.Equal(7, new ConfigStore(h.Paths).Load().SessionCap); }

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

    [Fact] public void SessionSkillsHash_RoundTrips_AndDefaultsToNull()
    { using var home = new TempHome(); var s = new ConfigStore(home.Paths);
      Assert.Null(s.Load().SessionSkillsHash);
      var hash = new string('a', 64);
      s.Save(new FyrdConfig(SessionSkillsHash: hash)); Assert.Equal(hash, s.Load().SessionSkillsHash); }

    [Theory, InlineData("abc"), InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789"), InlineData("")]
    public void SessionSkillsHash_ThatIsNotALowercaseSha256_IsRejectedOnSave_AndDroppedOnLoad(string hash)
    { using var home = new TempHome(); var s = new ConfigStore(home.Paths);
      Assert.Throws<ArgumentException>(() => s.Save(new FyrdConfig(SessionSkillsHash: hash)));
      File.WriteAllText(home.Paths.ConfigFile, $"{{\"sessionSkillsHash\": \"{hash}\"}}");
      Assert.Null(s.Load().SessionSkillsHash); }

    /// <summary>Update reads, changes and writes as one step: changes made at the same time to different fields all land.</summary>
    [Fact] public void Update_AppliesTheChangeToTheStoredConfig_AndConcurrentUpdatesDoNotLoseEachOther()
    { using var home = new TempHome(); var s = new ConfigStore(home.Paths);
      s.Save(new FyrdConfig(EnvironmentId: "env_a", SessionCap: 1));
      var saved = s.Update(c => c with { SessionSkillsHash = new string('b', 64) });
      Assert.Equal(new FyrdConfig(EnvironmentId: "env_a", SessionCap: 1, SessionSkillsHash: new string('b', 64)), saved);
      Assert.Equal(saved, s.Load());
      Parallel.For(0, 40, i => s.Update(c => i % 2 == 0 ? c with { SessionCap = c.SessionCap + 1 } : c with { SessionDailyLimitUsd = c.SessionDailyLimitUsd + 1 }));
      Assert.Equal(21, s.Load().SessionCap);
      Assert.Equal(FyrdConfig.DefaultSessionDailyLimitUsd + 20, s.Load().SessionDailyLimitUsd);
      // A change the store would refuse is not written.
      Assert.Throws<ArgumentException>(() => s.Update(c => c with { SessionSkillsHash = "nope" }));
      Assert.Equal(new string('b', 64), s.Load().SessionSkillsHash); }
}

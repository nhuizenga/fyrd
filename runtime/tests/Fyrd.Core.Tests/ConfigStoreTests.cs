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

    [Fact] public void Resolve_PrefersConfiguredOverEnv()
    { using var h = new TempHome(); Assert.Equal(h.Paths.Root, FyrdPaths.Resolve(h.Paths.Root).Root); }
}

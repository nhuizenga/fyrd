using Fyrd.Core.Config;
using Fyrd.Core.Hub;

namespace Fyrd.Core.Tests;

public class SecretStoreTests
{
    [Fact]
    public void SetGetDelete_RoundTrips_AndNeverWritesPlainText()
    {
        using var h = new TempHome();
        var store = new SecretStore(h.Paths, new FakeSecretProtector());
        Assert.False(store.Has(SecretStore.AnthropicApiKey));
        store.Set(SecretStore.AnthropicApiKey, "  sk-ant-secret  ");
        Assert.True(store.Has(SecretStore.AnthropicApiKey));
        Assert.Equal("sk-ant-secret", store.Get(SecretStore.AnthropicApiKey));
        var raw = File.ReadAllText(Path.Combine(h.Paths.SecretsDir, "anthropic-api-key.bin"));
        Assert.DoesNotContain("sk-ant-secret", raw);
        store.Delete(SecretStore.AnthropicApiKey);
        Assert.False(store.Has(SecretStore.AnthropicApiKey));
        Assert.Null(store.Get(SecretStore.AnthropicApiKey));
    }

    [Fact]
    public void BlankValue_AndUnknownName_Rejected()
    {
        using var h = new TempHome();
        var store = new SecretStore(h.Paths, new FakeSecretProtector());
        Assert.Throws<SecretStoreException>(() => store.Set(SecretStore.GitHubToken, "  "));
        Assert.Throws<ArgumentException>(() => store.Set("../evil", "x"));
    }

    [Fact]
    public void SavedToken_OverridesEnvironment_AndRemovingItReverts()
    {
        var token = new GitHubToken("env-token");
        Assert.Equal("env-token", token.Value);
        var changes = 0;
        token.Changed += () => changes++;
        token.SetSaved("saved-token");
        Assert.Equal("saved-token", token.Value);
        token.SetSaved(null);
        Assert.Equal("env-token", token.Value);
        Assert.Equal(2, changes);
        Assert.False(new GitHubToken(null).Present);
    }

    [Fact]
    public void Dpapi_OffWindows_SaysUnsupported()
    {
        if (OperatingSystem.IsWindows()) return;
        var p = new DpapiSecretProtector();
        Assert.False(p.Supported);
        Assert.Throws<SecretStoreException>(() => p.Protect([1]));
    }
}

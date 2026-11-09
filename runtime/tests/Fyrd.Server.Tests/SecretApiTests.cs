using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fyrd.Core.Config;
using Fyrd.Core.Hub;
using Microsoft.Extensions.DependencyInjection;

namespace Fyrd.Server.Tests;

public class SecretApiTests
{
    private const string Secret = "ghp_supersecretvalue";

    private static async Task<string> Body(HttpResponseMessage r) => await r.Content.ReadAsStringAsync();

    [Fact]
    public async Task Put_SavesAndOverridesToken_ResponsesNeverContainTheValue()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        var put = await c.PutAsJsonAsync("/api/secrets/github-token", new { value = Secret });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var text = await Body(put);
        Assert.DoesNotContain(Secret, text);
        var doc = JsonDocument.Parse(text).RootElement;
        var gh = doc.GetProperty("secrets").EnumerateArray().First(s => s.GetProperty("name").GetString() == "github-token");
        Assert.True(gh.GetProperty("set").GetBoolean());
        Assert.Equal("saved", gh.GetProperty("source").GetString());
        Assert.Equal(Secret, f.Services.GetRequiredService<GitHubToken>().Value);
        Assert.DoesNotContain(Secret, await Body(await c.GetAsync("/api/secrets")));
    }

    [Fact]
    public async Task Delete_RemovesSavedValue_AndRevertsToEnvironment()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        await c.PutAsJsonAsync("/api/secrets/anthropic-api-key", new { value = "sk-ant-x" });
        Assert.True(f.Services.GetRequiredService<SecretStore>().Has(SecretStore.AnthropicApiKey));
        var del = await c.DeleteAsync("/api/secrets/anthropic-api-key");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        Assert.False(f.Services.GetRequiredService<SecretStore>().Has(SecretStore.AnthropicApiKey));
        await c.PutAsJsonAsync("/api/secrets/github-token", new { value = Secret });
        await c.DeleteAsync("/api/secrets/github-token");
        Assert.NotEqual(Secret, f.Services.GetRequiredService<GitHubToken>().Present ? f.Services.GetRequiredService<GitHubToken>().Value : null);
    }

    [Fact]
    public async Task UnknownName_And_BlankValue_Rejected()
    {
        using var f = new FyrdFactory();
        var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await c.PutAsJsonAsync("/api/secrets/nope", new { value = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/api/secrets/github-token", new { value = " " })).StatusCode);
    }
}

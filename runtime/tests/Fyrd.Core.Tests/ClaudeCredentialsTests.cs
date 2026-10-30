using Fyrd.Core.CloudSessions;

namespace Fyrd.Core.Tests;

public sealed class ClaudeCredentialsTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "fyrd-cred-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
    }

    private void WriteCreds(string token, long expiresAt = 1_800_000_000_000)
    {
        Directory.CreateDirectory(Path.Combine(_home, ".claude"));
        File.WriteAllText(Path.Combine(_home, ".claude", ".credentials.json"),
            $$$"""{"claudeAiOauth":{"accessToken":"{{{token}}}","expiresAt":{{{expiresAt}}}}}""");
    }

    private void WriteAccount()
    {
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, ".claude.json"),
            """{"oauthAccount":{"organizationUuid":"org-123"}}""");
    }

    [Fact]
    public void Read_ReturnsTokenOrgAndExpiry()
    {
        WriteCreds("tok");
        WriteAccount();

        var auth = new ClaudeCredentials(_home).Read();

        Assert.Equal("tok", auth.AccessToken);
        Assert.Equal("org-123", auth.OrganizationUuid);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000), auth.ExpiresAt);
    }

    [Fact]
    public void Read_MissingCredentialsFile_ThrowsAuthMissing()
    {
        Directory.CreateDirectory(_home);
        WriteAccount();
        var ex = Assert.Throws<ClaudeAuthMissingException>(() => new ClaudeCredentials(_home).Read());
        Assert.Equal("Sign in to Claude Code with `claude auth login`.", ex.Message);
    }

    [Fact]
    public void Read_MissingOrgField_ThrowsAuthMissing()
    {
        WriteCreds("tok");
        File.WriteAllText(Path.Combine(_home, ".claude.json"), "{}");
        Assert.Throws<ClaudeAuthMissingException>(() => new ClaudeCredentials(_home).Read());
    }

    [Fact]
    public void Read_RereadsFileEachCall()
    {
        WriteAccount();
        var creds = new ClaudeCredentials(_home);
        WriteCreds("tok1");
        Assert.Equal("tok1", creds.Read().AccessToken);
        WriteCreds("tok2");
        Assert.Equal("tok2", creds.Read().AccessToken);
    }
}

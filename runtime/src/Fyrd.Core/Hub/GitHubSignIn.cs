namespace Fyrd.Core.Hub;

/// <summary>
/// Whether Fyrd can talk to GitHub, and as whom. Holds the login only, never the token. Refreshed at startup and every
/// 10 minutes, so an expired token shows up as <see cref="Error"/>.
/// </summary>
public sealed class GitHubSignIn(IGitHub github)
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    private volatile string? _login;
    private volatile string? _error;

    public bool Configured => github.Configured;
    public string? Login => _login;
    public string? Error => _error;

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (!github.Configured) { _login = null; _error = null; return; }
        try
        {
            _login = await github.LoginAsync(ct);
            _error = null;
        }
        catch (GitHubException e)
        {
            _login = null;
            _error = e.Message;
        }
    }
}

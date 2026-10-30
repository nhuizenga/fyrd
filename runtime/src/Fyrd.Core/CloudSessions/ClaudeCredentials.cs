using System.Text.Json;

namespace Fyrd.Core.CloudSessions;

public sealed record ClaudeAuth(string AccessToken, string OrganizationUuid, DateTimeOffset ExpiresAt);

public sealed class ClaudeAuthMissingException()
    : Exception("Sign in to Claude Code with `claude auth login`.");

/// <summary>
/// Reads the claude.ai OAuth credentials Claude Code keeps on disk. Files are re-read on every
/// call so a token refreshed by the CLI is picked up; the token is never cached or logged.
/// </summary>
public sealed class ClaudeCredentials
{
    private readonly string _home;

    public ClaudeCredentials(string? claudeHome = null) =>
        _home = claudeHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public ClaudeAuth Read()
    {
        var oauth = Navigate(Path.Combine(_home, ".claude", ".credentials.json"), "claudeAiOauth");
        var account = Navigate(Path.Combine(_home, ".claude.json"), "oauthAccount");

        var token = oauth.GetStringOrNull("accessToken");
        var org = account.GetStringOrNull("organizationUuid");
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(org)
            || oauth is not { } o || !o.TryGetProperty("expiresAt", out var exp)
            || exp.ValueKind != JsonValueKind.Number || !exp.TryGetInt64(out var ms))
            throw new ClaudeAuthMissingException();

        return new ClaudeAuth(token, org, DateTimeOffset.FromUnixTimeMilliseconds(ms));
    }

    // Returns the named top-level object of a JSON file, or null if file/property is missing or malformed.
    private static JsonElement? Navigate(string path, string property)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty(property, out var el)
                   && el.ValueKind == JsonValueKind.Object
                ? el.Clone()
                : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

internal static class JsonElementExtensions
{
    public static string? GetStringOrNull(this JsonElement? element, string property) =>
        element is { } e && e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}

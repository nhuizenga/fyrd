using System.Net.Http.Headers;
using System.Text.Json;

namespace Fyrd.Core.CloudSessions;

/// <summary>A file held in the Anthropic Files API, as returned by an upload.</summary>
public sealed record UploadedFile(string Id, string Filename, string MimeType, long SizeBytes, DateTimeOffset? ExpiresAt);

/// <summary>Uploads owner-attached files to the Files API; implemented by <see cref="FilesApi"/>.</summary>
public interface IFiles
{
    /// <summary>Uploads one file; it expires on its own after <see cref="FilesApi.ExpiresInSeconds"/>.</summary>
    Task<UploadedFile> UploadAsync(Stream content, string fileName, string? mimeType, CancellationToken ct = default);
}

/// <summary>
/// <c>POST /v1/files</c> with Fyrd's Claude OAuth token. The file cannot be scoped to a session, so it expires after
/// an hour and the session is told its id (see <see cref="Fyrd.Core.Hub.Attachments"/>).
/// </summary>
public sealed class FilesApi(HttpClient http, ClaudeCredentials credentials) : IFiles
{
    public const int ExpiresInSeconds = 3600;
    public const string ApiVersion = "2026-10-21";
    private static readonly Uri Endpoint = new("https://api.anthropic.com/v1/files");

    public async Task<UploadedFile> UploadAsync(Stream content, string fileName, string? mimeType, CancellationToken ct = default)
    {
        var auth = credentials.Read();
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(content);
        file.Headers.ContentType = MediaTypeHeaderValue.TryParse(mimeType, out var ct2)
            ? ct2 : new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        form.Add(new StringContent(ExpiresInSeconds.ToString()), "expires_in_seconds");

        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        req.Headers.Add("anthropic-version", ApiVersion);
        // OAuth tokens (as opposed to API keys) are only honoured on api.anthropic.com with this beta flag.
        req.Headers.Add("anthropic-beta", "oauth-2025-04-20");

        using var resp = await http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        var status = (int)resp.StatusCode;
        if (status is 401 or 403) throw new CloudSessionAuthException(status);
        if (status is < 200 or >= 300) throw new CloudSessionApiException(status, text);

        try
        {
            using var doc = JsonDocument.Parse(text);
            var r = doc.RootElement;
            return new UploadedFile(
                r.GetProperty("id").GetString() ?? throw new CloudSessionApiException(status, "<no file id>"),
                r.TryGetProperty("filename", out var n) && n.GetString() is { Length: > 0 } fn ? fn : fileName,
                r.TryGetProperty("mime_type", out var m) && m.GetString() is { Length: > 0 } mt ? mt : (mimeType ?? "application/octet-stream"),
                r.TryGetProperty("size_bytes", out var s) && s.TryGetInt64(out var sz) ? sz : 0,
                r.TryGetProperty("expires_at", out var e) && e.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(e.GetString(), out var exp) ? exp : null);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new CloudSessionApiException(status, "<unexpected upload response>");
        }
    }
}

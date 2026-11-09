using Fyrd.Core.CloudSessions;

namespace Fyrd.Core.Tests;

public sealed class FakeFiles : IFiles
{
    private int _n;

    public List<(string Name, string? Mime, long Length)> Uploads { get; } = [];

    public Exception? Fail { get; set; }

    public async Task<UploadedFile> UploadAsync(Stream content, string fileName, string? mimeType, CancellationToken ct = default)
    {
        if (Fail is not null) throw Fail;
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        Uploads.Add((fileName, mimeType, ms.Length));
        return new UploadedFile($"file_{++_n}", fileName, mimeType ?? "application/octet-stream", ms.Length, null);
    }
}

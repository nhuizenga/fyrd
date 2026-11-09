using System.Globalization;
using System.Text;
using Fyrd.Core.CloudSessions;

namespace Fyrd.Core.Hub;

/// <summary>Turns attached files into the plain-text instructions that tell the session to download them.</summary>
public static class Attachments
{
    /// <summary>
    /// <paramref name="text"/> followed by one block naming each file and how to fetch it. Files are not scoped to the
    /// session, so the session must be told explicitly. No credential is ever written into the text.
    /// </summary>
    public static string Append(string text, IReadOnlyList<UploadedFile> files)
    {
        if (files.Count == 0) return text;
        var sb = new StringBuilder(text.TrimEnd());
        if (sb.Length > 0) sb.Append("\n\n");
        sb.Append("The owner attached ").Append(files.Count == 1 ? "a file" : $"{files.Count} files")
          .Append(" to this message. Download each one before you read it: ").Append(FilesApi.DownloadRequest)
          .Append(", authenticated with your own Anthropic credentials. Files are deleted about an hour after upload.");
        foreach (var f in files)
        {
            sb.Append("\n- \"").Append(SafeName(f.Filename)).Append("\" (").Append(SafeMime(f.MimeType)).Append(", ")
              .Append(Size(f.SizeBytes)).Append("): file id ").Append(f.Id);
        }
        return sb.ToString();
    }

    private static string SafeName(string name)
    {
        // Names reach the agent as text, so drop anything that could end the quoted name or hide/reorder it: control,
        // format (bidi overrides, zero-width), line/paragraph separators, unassigned and private-use characters.
        var sb = new StringBuilder();
        foreach (var r in name.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(r) is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator or UnicodeCategory.OtherNotAssigned or UnicodeCategory.PrivateUse
                or UnicodeCategory.Surrogate) continue;
            if (r.Value is '`' or '"' or '\'' or '<' or '>' or '\\') continue;
            sb.Append(r.ToString());
        }
        var clean = sb.ToString().Trim();
        if (clean.Length > 200) clean = clean[..200];
        return clean.Length == 0 ? "file" : clean;
    }

    private static string SafeMime(string mime) =>
        mime.Length <= 100 && mime.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '+' or '-') ? mime : "application/octet-stream";

    private static string Size(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024.0:0.#} MB" : bytes >= 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes} bytes";
}

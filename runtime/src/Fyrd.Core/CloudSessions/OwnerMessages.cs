using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fyrd.Core.CloudSessions;

/// <summary>
/// Sends a message to a cloud session as the owner's turn. Every message Fyrd sends directly to a session goes through
/// here (typed turns, prompts, notices, relayed answers): the cloud HTTP API's message post is read as a peer's and
/// running sessions reject it, so <see cref="ICloudSessions"/> has no send method.
/// </summary>
public interface IOwnerMessages
{
    Task SendAsync(string sessionId, string text, CancellationToken ct = default);

    /// <summary>
    /// Creates a new cloud session as the owner from <paramref name="workingDirectory"/>. <paramref name="description"/>
    /// becomes its title and its first turn; send the real opening message with <see cref="SendAsync"/> afterwards.
    /// Returns the new session's <c>session_…</c> id.
    /// </summary>
    Task<string> CreateAsync(string description, string workingDirectory, CancellationToken ct = default);
}

public sealed class OwnerMessageException(string message) : Exception(message);

/// <summary>
/// Queues the turn with the owner's signed-in <c>claude</c> CLI (<c>claude -p --cloud &lt;id&gt;</c>, message on stdin),
/// and creates sessions with <c>claude --cloud &lt;description&gt;</c> in a pseudo terminal (see <see cref="ITerminalRunner"/>).
/// The CLI's turns are stamped with its own client platform and read as the owner's, with no peer wrapper; turns posted
/// to the HTTP API carry no platform and are demoted to peer. See docs/superpowers/spikes/2026-11-02-owner-turn-probe.md.
/// Fyrd never sets that platform header itself.
/// </summary>
public sealed partial class ClaudeCliOwnerMessages : IOwnerMessages
{
    // Delivery is at-least-once: on a timeout the CLI may already have queued the turn, yet callers (HubRunner hashes,
    // CostGuard notice markers, LeadRelay failed cards) only record success after SendAsync returns, so they retry and can
    // post the same message twice. Accepted: a duplicate prompt or notice is harmless next to a lost one.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    [GeneratedRegex("^session_[A-Za-z0-9]+$")]
    private static partial Regex SessionIdPattern();

    /// <summary>The CLI arguments. The message goes on stdin, so it is never parsed as an argument or quoted.</summary>
    public static IReadOnlyList<string> Args(string sessionId) =>
        SessionIdPattern().IsMatch(sessionId)
            ? ["-p", "--cloud", sessionId, "--output-format", "json"]
            : throw new ArgumentException("Not a cloud session ID.", nameof(sessionId));

    /// <summary>
    /// Accepts the CLI's <c>{"ok":true,…}</c> output; otherwise throws with its <c>error</c>, its stderr, or its exit code.
    /// Stderr alone is not a failure: the owner's SessionEnd hooks may write to it.
    /// </summary>
    public static void CheckResult(int exitCode, string stdout, string stderr)
    {
        bool? ok = null;
        string? error = null;
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var r = doc.RootElement;
            if (r.ValueKind == JsonValueKind.Object)
            {
                if (r.TryGetProperty("ok", out var o)) ok = o.ValueKind == JsonValueKind.True;
                if (r.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String) error = e.GetString();
            }
        }
        catch (JsonException) { }

        if (exitCode == 0 && ok == true) return;
        if (!string.IsNullOrWhiteSpace(error)) throw new OwnerMessageException(error);
        var err = stderr.Trim();
        throw new OwnerMessageException(err.Length > 0 ? err : $"claude exited with {exitCode}.");
    }

    public async Task SendAsync(string sessionId, string text, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        var p = Start(Args(sessionId), workingDirectory: null);
        using (p)
        {
            var stdout = p.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = p.StandardError.ReadToEndAsync(cts.Token);
            try
            {
                try
                {
                    await p.StandardInput.WriteAsync(text.AsMemory(), cts.Token);
                    p.StandardInput.Close();
                }
                catch (IOException)
                {
                    // The CLI exited before reading stdin (signed out, bad session): fall through so its real error surfaces.
                }
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch (InvalidOperationException) { }
                // Observe the read tasks so they cannot fault unobserved after the kill.
                try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
                if (ct.IsCancellationRequested) throw;
                throw new OwnerMessageException("claude timed out; the message may still have been delivered.");
            }
            CheckResult(p.ExitCode, await stdout, await stderr);
        }
    }

    private static Process Start(IReadOnlyList<string> args, string? workingDirectory)
    {
        var psi = new ProcessStartInfo("claude")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            // Fyrd's server may run without a console: the CLI must not open one that steals focus.
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
        };
        if (workingDirectory is not null) psi.WorkingDirectory = workingDirectory;
        foreach (var a in args) psi.ArgumentList.Add(a);
        try { return Process.Start(psi) ?? throw new OwnerMessageException("Could not start claude."); }
        catch (System.ComponentModel.Win32Exception) { throw new OwnerMessageException("The claude CLI is not installed or not on PATH."); }
    }

    // ---- Creating a session ----------------------------------------------------------------------------------------

    /// <summary>How long <c>claude --cloud</c> may take to create the session, by default, before Fyrd gives up and kills it.</summary>
    public static readonly TimeSpan DefaultCreateTimeout = TimeSpan.FromMinutes(3);

    // CSI (ESC [ … final), OSC (ESC ] … BEL or ESC \), DCS/SOS/PM/APC strings (ESC P|X|^|_ … ESC \ or BEL), other
    // two-character escapes, and stray control characters. A CSI that moves the cursor to another line (CUP, VPA, CUU, CUD,
    // CNL, CPL) becomes a line break, and one that moves along the line (CUF, CHA) a space: a pseudo terminal may lay out
    // lines with cursor moves instead of line breaks, and stripping those would run the lines together.
    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)|\x1B[PX^_][^\x07\x1B]*(?:\x07|\x1B\\)|\x1B[@-Z\\-_]|[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]")]
    private static partial Regex EscapePattern();

    // The id in the session's claude.ai/code URL. The id must be followed by something that ends it (the URL's query
    // string, a path, a quote or white space), so a line still being printed, or text run on after it, can't lengthen
    // it. Group 2 is that ending: a query string or path (printed after the id, so the id is whole) is a firm ending;
    // white space may be a cursor move in the middle of the id, so the id may be cut off.
    [GeneratedRegex(@"claude\.ai/code/(?:session|cse)_([A-Za-z0-9]+)(?=([?#/\s""'<>)\]]))")]
    private static partial Regex SessionUrlPattern();

    // The id after --teleport, followed by white space.
    [GeneratedRegex(@"--teleport\s+(?:session|cse)_([A-Za-z0-9]+)(?=\s)")]
    private static partial Regex TeleportPattern();

    private static string Unescape(Match m)
    {
        var v = m.Value;
        if (v.Length > 2 && v[0] == '\x1B' && v[1] == '[')
            return v[^1] switch { 'H' or 'f' or 'd' or 'A' or 'B' or 'E' or 'F' => "\n", 'C' or 'G' => " ", _ => "" };
        return "";
    }

    private readonly ITerminalRunner _terminal;
    private readonly TimeSpan _createTimeout;

    public ClaudeCliOwnerMessages() : this(TerminalRunners.ForThisPlatform()) { }

    /// <param name="createTimeout">How long a create may take (<see cref="DefaultCreateTimeout"/> when null).</param>
    public ClaudeCliOwnerMessages(ITerminalRunner terminal, TimeSpan? createTimeout = null)
    {
        _terminal = terminal;
        _createTimeout = createTimeout ?? DefaultCreateTimeout;
    }

    /// <summary>
    /// The command that creates a session: <c>claude --cloud &lt;description&gt;</c>. The CLI only creates a session from
    /// an interactive terminal (with <c>-p</c>, or without a terminal, <c>--cloud</c> only messages an existing session),
    /// so it runs in a pseudo terminal. The description becomes the session's title and its first turn.
    /// </summary>
    public static IReadOnlyList<string> CreateCommand(string description)
    {
        var d = (description ?? "").Trim();
        if (d.Length == 0 || d.StartsWith('-') || !d.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_'))
            throw new ArgumentException("Not a usable session description.", nameof(description));
        return ["claude", "--cloud", d];
    }

    /// <summary>Terminal output as plain lines: escape sequences removed, carriage returns treated as line breaks.</summary>
    public static IReadOnlyList<string> PlainLines(string output) =>
        PlainText(output).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    /// <summary>Terminal output as plain text: escape sequences removed, line moves and carriage returns as line breaks.</summary>
    public static string PlainText(string output) =>
        EscapePattern().Replace(output ?? "", Unescape).Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>The session ids <c>claude --cloud</c> has printed so far (each <c>session_…</c>; a <c>cse_…</c> id is the same session).</summary>
    /// <param name="FirmUrl">The last id in a claude.ai/code URL followed by its query string or a path: printed whole.</param>
    /// <param name="Url">The last id in a URL followed by white space only, which may be a cursor move inside the id.</param>
    /// <param name="Teleport">The last id after <c>--teleport</c>, followed by white space.</param>
    public sealed record CreatedIds(string? FirmUrl, string? Url, string? Teleport);

    /// <summary>Reads the ids from what <c>claude --cloud</c> printed (escape sequences removed, lines run together included).</summary>
    public static CreatedIds ReadCreatedIds(string output)
    {
        var text = PlainText(output);
        string? firm = null, loose = null, teleport = null;
        foreach (Match m in SessionUrlPattern().Matches(text))
        {
            var id = "session_" + m.Groups[1].Value;
            if (m.Groups[2].Value is "?" or "#" or "/") firm = id; else loose = id;
        }
        foreach (Match m in TeleportPattern().Matches(text)) teleport = "session_" + m.Groups[1].Value;
        return new CreatedIds(firm, loose, teleport);
    }

    /// <summary>
    /// Whether the CLI has printed the new session's id in full, so Fyrd can stop it without waiting for it to exit: a URL
    /// id followed by its query string, or a URL id that the <c>--teleport</c> line (printed after it) repeats. A URL id
    /// that may be cut off by a cursor move doesn't count.
    /// </summary>
    public static bool CreateFinished(string output)
    {
        var ids = ReadCreatedIds(output);
        return ids.FirmUrl is not null || ids.Teleport is not null && ids.Teleport == ids.Url;
    }

    /// <summary>
    /// The new session's id from what <c>claude --cloud</c> printed. The CLI (2.1.326) prints
    /// <code>
    /// Created cloud session: Controller
    /// View: https://claude.ai/code/session_…?from=cli&amp;m=0
    /// Resume with: claude --teleport session_…
    /// </code>
    /// and exits. A URL id followed by its query string is used (a <c>--teleport</c> id that only runs on into later text
    /// is ignored); otherwise the URL and <c>--teleport</c> ids must agree. A lone id is used only when
    /// <paramref name="exited"/> (the CLI finished printing by itself). Null when there is no id yet; throws
    /// <see cref="OwnerMessageException"/> when the CLI printed two different ids.
    /// </summary>
    public static string? FindCreatedId(string output, bool exited = true)
    {
        var ids = ReadCreatedIds(output);
        if (ids.FirmUrl is { } firm)
        {
            if (ids.Teleport is { } t && t != firm && !t.StartsWith(firm, StringComparison.Ordinal)) throw Mismatch(firm, t);
            return firm;
        }
        if (ids.Url is { } url && ids.Teleport is { } teleport)
            return url == teleport ? url : throw Mismatch(url, teleport);
        return exited ? ids.Url ?? ids.Teleport : null;
    }

    private static OwnerMessageException Mismatch(string a, string b) =>
        new($"claude printed two different session ids ({a} and {b}), so Fyrd can't tell which one it created. Find the " +
            "new \"Controller\" session on claude.ai/code, archive it, and create the Controller again.");

    /// <summary>
    /// The reason the CLI is waiting for an answer nobody will give (it runs in a hidden terminal), or null. Fyrd types
    /// nothing into it, so a first-run prompt is reported to the owner, who answers it once in a terminal of their own.
    /// </summary>
    public static string? FindBlockingPrompt(string output, string workingDirectory)
    {
        var text = string.Join('\n', PlainLines(output));
        if (text.Contains("trust this folder", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Quick safety check", StringComparison.OrdinalIgnoreCase))
            return $"claude asked whether to trust the folder it runs in ({workingDirectory}). Open a terminal there, run " +
                   "`claude`, choose \"Yes, I trust this folder\", then type /exit, and create the Controller again.";
        if (text.Contains("Choose the text style", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Select login method", StringComparison.OrdinalIgnoreCase))
            return "claude is asking first-run setup questions. Run `claude` once in a terminal, finish its setup, then " +
                   "type /exit and create the Controller again.";
        return null;
    }

    /// <summary>
    /// Creates a session with <c>claude --cloud &lt;description&gt;</c> in a pseudo terminal, run in
    /// <paramref name="workingDirectory"/> (a folder that is not a git repository, so nothing is copied into the session),
    /// and returns its id from what the CLI prints. Throws with the CLI's own words when it prints no id.
    /// </summary>
    public async Task<string> CreateAsync(string description, string workingDirectory, CancellationToken ct = default)
    {
        var command = CreateCommand(description);
        Directory.CreateDirectory(workingDirectory);
        var run = await _terminal.RunAsync(command, workingDirectory,
            output => CreateFinished(output) || FindBlockingPrompt(output, workingDirectory) is not null,
            _createTimeout, ct);

        // ExitCode is null when Fyrd stopped the CLI (or it timed out); then only an id printed in full is used.
        if (FindCreatedId(run.Output, exited: run.ExitCode is not null) is { } id) return id;
        if (FindBlockingPrompt(run.Output, workingDirectory) is { } prompt) throw new OwnerMessageException(prompt);
        var tail = string.Join(' ', PlainLines(run.Output).TakeLast(4));
        if (run.TimedOut)
            throw new OwnerMessageException("claude timed out creating the session; it may still have been created (check claude.ai/code)." +
                                            (tail.Length > 0 ? $" It last printed: {tail}" : ""));
        throw new OwnerMessageException(tail.Length > 0
            ? $"claude didn't create a session: {tail}"
            : $"claude exited with {run.ExitCode?.ToString() ?? "no exit code"} without creating a session.");
    }
}

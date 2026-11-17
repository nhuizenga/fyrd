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

    /// <summary>How long <c>claude --cloud</c> may take to create the session before Fyrd gives up and kills it.</summary>
    public static TimeSpan CreateTimeout { get; set; } = TimeSpan.FromMinutes(3);

    [GeneratedRegex(@"\b(session|cse)_([A-Za-z0-9]+)\b")]
    private static partial Regex AnySessionIdPattern();

    // CSI (ESC [ … final), OSC (ESC ] … BEL or ESC \), other two-character escapes, and stray control characters.
    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)|\x1B[@-Z\\-_]|[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]")]
    private static partial Regex EscapePattern();

    private readonly ITerminalRunner _terminal;

    public ClaudeCliOwnerMessages() : this(TerminalRunners.ForThisPlatform()) { }

    public ClaudeCliOwnerMessages(ITerminalRunner terminal) => _terminal = terminal;

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

    /// <summary>The <c>session_…</c> id in <paramref name="text"/> (a <c>cse_…</c> id is the same session), or null.</summary>
    public static string? FindSessionId(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var m = AnySessionIdPattern().Match(text);
        return m.Success ? "session_" + m.Groups[2].Value : null;
    }

    /// <summary>Terminal output as plain lines: escape sequences removed, carriage returns treated as line breaks.</summary>
    public static IReadOnlyList<string> PlainLines(string output) =>
        EscapePattern().Replace(output ?? "", "")
            .Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    /// <summary>
    /// The new session's id from what <c>claude --cloud</c> printed, once it has printed it. The CLI (2.1.326) prints
    /// <code>
    /// Created cloud session: Controller
    /// View: https://claude.ai/code/session_…?from=cli&amp;m=0
    /// Resume with: claude --teleport session_…
    /// </code>
    /// and exits. The id is read from the <c>View:</c> or <c>Resume with:</c> line; null until one is printed.
    /// </summary>
    public static string? FindCreatedId(string output)
    {
        var lines = PlainLines(output);
        var created = lines.ToList().FindIndex(l => l.StartsWith("Created cloud session", StringComparison.OrdinalIgnoreCase));
        foreach (var line in created >= 0 ? lines.Skip(created) : lines)
            if ((line.StartsWith("View:", StringComparison.OrdinalIgnoreCase) && line.Contains("claude.ai/code/")) ||
                line.StartsWith("Resume with:", StringComparison.OrdinalIgnoreCase))
                if (FindSessionId(line) is { } id) return id;
        return null;
    }

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
        TerminalRun run;
        try
        {
            run = await _terminal.RunAsync(command, workingDirectory,
                output => FindCreatedId(output) is not null || FindBlockingPrompt(output, workingDirectory) is not null,
                CreateTimeout, ct);
        }
        catch (System.ComponentModel.Win32Exception) { throw new OwnerMessageException("The claude CLI is not installed or not on PATH."); }

        if (FindCreatedId(run.Output) is { } id) return id;
        if (FindBlockingPrompt(run.Output, workingDirectory) is { } prompt) throw new OwnerMessageException(prompt);
        if (run.TimedOut)
            throw new OwnerMessageException("claude timed out creating the session; it may still have been created (check claude.ai/code).");
        var tail = string.Join(' ', PlainLines(run.Output).TakeLast(4));
        throw new OwnerMessageException(tail.Length > 0
            ? $"claude didn't create a session: {tail}"
            : $"claude exited with {run.ExitCode?.ToString() ?? "no exit code"} without creating a session.");
    }
}

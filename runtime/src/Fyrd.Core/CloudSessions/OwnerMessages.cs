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
    /// Creates a new cloud session as the owner, named <paramref name="name"/>, whose first turn is <paramref name="text"/>
    /// (read as the owner's turn, like <see cref="SendAsync"/>). Returns the new session's <c>session_…</c> id.
    /// </summary>
    Task<string> CreateAsync(string name, string text, CancellationToken ct = default);
}

public sealed class OwnerMessageException(string message) : Exception(message);

/// <summary>
/// Queues the turn with the owner's signed-in <c>claude</c> CLI (<c>claude -p --cloud &lt;id&gt;</c>, message on stdin),
/// and creates sessions the same way (<c>claude -p --cloud -n &lt;name&gt;</c>, first turn on stdin).
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

    /// <summary>How long a create may take before Fyrd knows the new session's id (or gives up and kills the CLI).</summary>
    public static TimeSpan CreateTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How long the CLI may keep running in the background after the id is known (it waits for the first turn).</summary>
    public static TimeSpan CreateTailTimeout { get; set; } = TimeSpan.FromMinutes(30);

    [GeneratedRegex(@"\b(session|cse)_([A-Za-z0-9]+)\b")]
    private static partial Regex AnySessionIdPattern();

    [GeneratedRegex(@"^Cloud session: (?:session|cse)_[A-Za-z0-9]+")]
    private static partial Regex CreatedNotePattern();

    /// <summary>
    /// The CLI arguments that create a session. <c>--cloud</c> with no value creates a new session; with <c>-p</c> its
    /// task is the prompt, read from stdin (so it is never parsed as an argument or quoted), and <c>-n</c> names it.
    /// </summary>
    public static IReadOnlyList<string> CreateArgs(string name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0 || n.StartsWith('-') || n.Any(char.IsControl))
            throw new ArgumentException("Not a usable session name.", nameof(name));
        return ["-p", "--cloud", "-n", n, "--output-format", "json"];
    }

    /// <summary>The <c>session_…</c> id in <paramref name="text"/> (a <c>cse_…</c> id is the same session), or null.</summary>
    public static string? FindSessionId(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var m = AnySessionIdPattern().Match(text);
        return m.Success ? "session_" + m.Groups[2].Value : null;
    }

    /// <summary>
    /// The new session's id from the CLI's stderr note <c>Cloud session: &lt;id&gt; (&lt;url&gt;)</c>, which it prints
    /// once the session is open, before the first turn ends; null for any other line.
    /// </summary>
    public static string? FindCreatedNote(string? line) =>
        line is not null && CreatedNotePattern().IsMatch(line.Trim()) ? FindSessionId(line) : null;

    /// <summary>
    /// The new session's id from the CLI's final output. With <c>-p --output-format json</c> the CLI waits for the first
    /// turn and prints its result object with the cloud <c>session_id</c> and <c>session_url</c> added (a self-hosted
    /// environment prints <c>{"ok":true,"session_id":…,"url":…}</c> instead); both are read. A session that was created
    /// but whose turn ended in an error still counts as created. Throws with the CLI's <c>error</c>, its stderr, or its
    /// exit code when no session id can be found.
    /// </summary>
    public static string ParseCreated(int exitCode, string stdout, string stderr)
    {
        string? id = null, error = null;
        bool? ok = null;
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var r = doc.RootElement;
            if (r.ValueKind == JsonValueKind.Object)
            {
                if (r.TryGetProperty("ok", out var o)) ok = o.ValueKind == JsonValueKind.True;
                if (r.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String) error = e.GetString();
                foreach (var key in new[] { "session_id", "session_url", "url" })
                    if (id is null && r.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                        id = FindSessionId(v.GetString());
            }
        }
        catch (JsonException) { }

        if (id is not null && ok != false) return id;
        if (!string.IsNullOrWhiteSpace(error)) throw new OwnerMessageException(error);
        // The CLI's note is the fallback when its stdout was not JSON.
        if (ok != false)
            foreach (var line in stderr.Split('\n'))
                if (FindCreatedNote(line) is { } noted) return noted;
        var err = stderr.Trim();
        throw new OwnerMessageException(err.Length > 0 ? err : $"claude exited with {exitCode} without creating a session.");
    }

    /// <summary>
    /// Creates the session with <c>claude -p --cloud -n &lt;name&gt;</c> and the text on stdin. Returns as soon as the
    /// id is known: from the CLI's stderr note while the first turn still runs (the CLI is then left to finish in the
    /// background; stopping it would not stop the session), or from its final output. It runs in the temp directory so
    /// no local repository is attached to the new session.
    /// </summary>
    public async Task<string> CreateAsync(string name, string text, CancellationToken ct = default)
    {
        var args = CreateArgs(name);
        var p = Start(args, Path.GetTempPath());
        var noted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrText = new StringBuilder();
        var stdout = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = Task.Run(async () =>
        {
            while (await p.StandardError.ReadLineAsync() is { } line)
            {
                lock (stderrText) stderrText.Append(line).Append('\n');
                if (FindCreatedNote(line) is { } id) noted.TrySetResult(id);
            }
        });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(CreateTimeout);
        var handedOff = false;
        try
        {
            try
            {
                await p.StandardInput.WriteAsync(text.AsMemory(), cts.Token);
                p.StandardInput.Close();
            }
            catch (IOException)
            {
                // The CLI exited before reading stdin (signed out, refused): fall through so its real error surfaces.
            }

            var exited = p.WaitForExitAsync(cts.Token);
            var first = await Task.WhenAny(noted.Task, exited);
            if (first == noted.Task)
            {
                handedOff = true;
                _ = FinishInBackgroundAsync(p, stdout, stderr);
                return await noted.Task;
            }
            await exited; // throws when cancelled or timed out
            try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None); } catch { }
            string err;
            lock (stderrText) err = stderrText.ToString();
            return ParseCreated(p.ExitCode, stdout.IsCompletedSuccessfully ? stdout.Result : "", err);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch (InvalidOperationException) { }
            try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None); } catch { }
            // The note may have arrived just as the wait ended.
            if (noted.Task.IsCompletedSuccessfully) return noted.Task.Result;
            if (ct.IsCancellationRequested) throw;
            throw new OwnerMessageException("claude timed out creating the session; it may still have been created (check claude.ai/code).");
        }
        finally
        {
            if (!handedOff) p.Dispose();
        }
    }

    /// <summary>Lets the create CLI wait for the first turn, then disposes it; kills it after <see cref="CreateTailTimeout"/>.</summary>
    private static async Task FinishInBackgroundAsync(Process p, Task<string> stdout, Task stderr)
    {
        using (p)
        {
            try { await p.WaitForExitAsync().WaitAsync(CreateTailTimeout); }
            catch (TimeoutException) { try { p.Kill(true); } catch (InvalidOperationException) { } }
            catch { }
            try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
    }
}

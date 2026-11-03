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
}

public sealed class OwnerMessageException(string message) : Exception(message);

/// <summary>
/// Queues the turn with the owner's signed-in <c>claude</c> CLI (<c>claude -p --cloud &lt;id&gt;</c>, message on stdin).
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
        var psi = new ProcessStartInfo("claude")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            // Fyrd's server may run without a console: the CLI must not open one that steals focus.
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var a in Args(sessionId)) psi.ArgumentList.Add(a);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        Process p;
        try { p = Process.Start(psi) ?? throw new OwnerMessageException("Could not start claude."); }
        catch (System.ComponentModel.Win32Exception) { throw new OwnerMessageException("The claude CLI is not installed or not on PATH."); }
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
}

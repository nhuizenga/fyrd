using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Fyrd.Core.CloudSessions;

/// <summary>What a command printed to its terminal, and how it ended.</summary>
/// <param name="ExitCode">Null when the command was stopped (by <c>stop</c>, the timeout or cancellation) rather than exiting.</param>
public sealed record TerminalRun(string Output, int? ExitCode, bool TimedOut);

/// <summary>
/// Runs a command attached to a pseudo terminal, so a CLI that refuses to work without an interactive terminal (such as
/// <c>claude --cloud &lt;description&gt;</c>, which only creates a session from one) sees a real TTY while Fyrd reads what it
/// prints. Nothing is typed into it.
/// </summary>
public interface ITerminalRunner
{
    /// <summary>
    /// Runs <paramref name="command"/> (file, then arguments) in <paramref name="workingDirectory"/>. After each chunk of
    /// output, <paramref name="stop"/> sees everything printed so far; once it returns true the command gets a moment to
    /// exit by itself and is then killed. It is also killed after <paramref name="timeout"/> (<c>TimedOut</c>) or when
    /// <paramref name="ct"/> is cancelled (which throws).
    /// </summary>
    Task<TerminalRun> RunAsync(IReadOnlyList<string> command, string workingDirectory, Func<string, bool> stop,
        TimeSpan timeout, CancellationToken ct = default);
}

public static class TerminalRunners
{
    /// <summary>ConPTY on Windows; <c>script</c> (util-linux or BSD) elsewhere.</summary>
    public static ITerminalRunner ForThisPlatform() =>
        OperatingSystem.IsWindows() ? new ConPtyTerminalRunner() : new ScriptTerminalRunner();

    /// <summary>How long a command may keep running after <c>stop</c> said it is done, before it is killed.</summary>
    internal static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(3);
}

/// <summary>
/// Collects a command's output, runs the stop check on each chunk, and signals when the command should be stopped.
/// Shared by both runners.
/// </summary>
internal sealed class TerminalOutput(Func<string, bool> stop)
{
    private readonly StringBuilder _text = new();
    private readonly TaskCompletionSource _stopRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task StopRequested => _stopRequested.Task;

    public void Append(string chunk)
    {
        string all;
        lock (_text) all = _text.Append(chunk).ToString();
        bool done;
        try { done = stop(all); } catch { done = false; }
        if (done) _stopRequested.TrySetResult();
    }

    public override string ToString() { lock (_text) return _text.ToString(); }
}

/// <summary>
/// Runs the command under <c>script</c>, which gives it a pseudo terminal on Linux and macOS. Used where Fyrd isn't on
/// Windows (and by the tests, with a fake <c>claude</c>).
/// </summary>
public sealed class ScriptTerminalRunner : ITerminalRunner
{
    /// <summary>The <c>script</c> invocation: util-linux takes one shell command string, BSD/macOS takes the argv.</summary>
    public static IReadOnlyList<string> ScriptArgs(IReadOnlyList<string> command, bool bsd) =>
        bsd
            ? ["-q", "/dev/null", .. command]
            : ["-qfec", string.Join(' ', command.Select(ShellQuote)), "/dev/null"];

    /// <summary>Single-quotes <paramref name="s"/> for a POSIX shell.</summary>
    public static string ShellQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    public async Task<TerminalRun> RunAsync(IReadOnlyList<string> command, string workingDirectory, Func<string, bool> stop,
        TimeSpan timeout, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo("script")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workingDirectory,
        };
        foreach (var a in ScriptArgs(command, OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())) psi.ArgumentList.Add(a);
        Process p;
        try { p = Process.Start(psi) ?? throw new OwnerMessageException("Could not start script."); }
        catch (System.ComponentModel.Win32Exception) { throw new OwnerMessageException("The script command is not installed, so Fyrd can't give claude a terminal."); }

        using (p)
        {
            var output = new TerminalOutput(stop);
            var pump = Task.Run(async () =>
            {
                var buf = new char[4096];
                int n;
                while ((n = await p.StandardOutput.ReadAsync(buf.AsMemory(), CancellationToken.None)) > 0)
                    output.Append(new string(buf, 0, n));
            });
            var errors = p.StandardError.ReadToEndAsync(CancellationToken.None);
            return await TerminalWait.RunAsync(
                p.WaitForExitAsync(CancellationToken.None), () => p.ExitCode,
                () => { try { p.Kill(true); } catch (InvalidOperationException) { } },
                output, Task.WhenAll(pump, errors), timeout, ct);
        }
    }
}

/// <summary>The wait/stop/kill logic both runners share.</summary>
internal static class TerminalWait
{
    public static async Task<TerminalRun> RunAsync(Task exited, Func<int> exitCode, Action kill, TerminalOutput output,
        Task drained, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var stopped = false;
        try
        {
            var first = await Task.WhenAny(exited, output.StopRequested, Task.Delay(Timeout.Infinite, cts.Token));
            if (first == output.StopRequested && !exited.IsCompleted)
            {
                await Task.WhenAny(exited, Task.Delay(TerminalRunners.ExitGrace, cts.Token));
                if (!exited.IsCompleted) { kill(); stopped = true; }
            }
            else if (first != exited && first != output.StopRequested)
            {
                await first; // throws when cancelled or timed out
            }
        }
        catch (OperationCanceledException)
        {
            kill();
            try { await drained.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None); } catch { }
            if (ct.IsCancellationRequested) throw;
            return new TerminalRun(output.ToString(), null, TimedOut: true);
        }

        try { await drained.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None); } catch { }
        return new TerminalRun(output.ToString(), stopped || !exited.IsCompleted ? null : exitCode(), TimedOut: false);
    }
}

/// <summary>
/// Runs the command in a Windows pseudo console (ConPTY), so it sees a real console while Fyrd, which may have no console
/// of its own, reads its output. The command runs through <c>cmd.exe /c</c>, which finds <c>claude</c> on PATH whether it
/// is <c>claude.exe</c> or an npm <c>claude.cmd</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ConPtyTerminalRunner : ITerminalRunner
{
    /// <summary>The <c>cmd.exe /d /s /c "…"</c> command line for <paramref name="command"/>; each argument is quoted.</summary>
    public static string CommandLine(IReadOnlyList<string> command)
    {
        foreach (var a in command)
            if (a.IndexOfAny(['"', '%', '^', '&', '|', '<', '>', '\r', '\n']) >= 0)
                throw new ArgumentException($"Unsafe command argument: {a}", nameof(command));
        var comspec = Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } c ? c : "cmd.exe";
        return $"\"{comspec}\" /d /s /c \"{string.Join(' ', command.Select(a => "\"" + a + "\""))}\"";
    }

    public async Task<TerminalRun> RunAsync(IReadOnlyList<string> command, string workingDirectory, Func<string, bool> stop,
        TimeSpan timeout, CancellationToken ct = default)
    {
        if (!Native.CreatePipe(out var inRead, out var inWrite, IntPtr.Zero, 0))
            throw new OwnerMessageException($"Could not create a terminal for claude (error {Marshal.GetLastWin32Error()}).");
        if (!Native.CreatePipe(out var outRead, out var outWrite, IntPtr.Zero, 0))
        {
            var error = Marshal.GetLastWin32Error();
            inRead.Dispose(); inWrite.Dispose();
            throw new OwnerMessageException($"Could not create a terminal for claude (error {error}).");
        }

        var hr = Native.CreatePseudoConsole(new Native.COORD { X = 120, Y = 40 }, inRead, outWrite, 0, out var hpc);
        // The pseudo console holds its own copies of its ends of the pipes.
        inRead.Dispose();
        outWrite.Dispose();
        if (hr != 0)
        {
            inWrite.Dispose(); outRead.Dispose();
            throw new OwnerMessageException($"Could not create a terminal for claude (HRESULT 0x{hr:X8}).");
        }

        var pcClosed = 0;
        void ClosePc() { if (Interlocked.Exchange(ref pcClosed, 1) == 0) Native.ClosePseudoConsole(hpc); }

        // The command and everything it starts (cmd.exe, then claude) run in a job that is killed as a whole, and when
        // Fyrd closes the job, so stopping cmd.exe can't leave claude running.
        var job = Native.CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            job.Dispose(); ClosePc(); inWrite.Dispose(); outRead.Dispose();
            throw new OwnerMessageException($"Could not create a job for claude (error {error}).");
        }

        Native.PROCESS_INFORMATION pi;
        var attrs = IntPtr.Zero;
        try
        {
            var limits = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            limits.BasicLimitInformation.LimitFlags = Native.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            if (!Native.SetInformationJobObject(job, Native.JobObjectExtendedLimitInformation, ref limits,
                    Marshal.SizeOf<Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
                throw new OwnerMessageException($"Could not set up a job for claude (error {Marshal.GetLastWin32Error()}).");

            var size = IntPtr.Zero;
            Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attrs = Marshal.AllocHGlobal(size);
            if (!Native.InitializeProcThreadAttributeList(attrs, 1, 0, ref size) ||
                !Native.UpdateProcThreadAttribute(attrs, 0, (IntPtr)Native.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, hpc,
                    (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new OwnerMessageException($"Could not attach claude to its terminal (error {Marshal.GetLastWin32Error()}).");

            var si = new Native.STARTUPINFOEX { lpAttributeList = attrs };
            si.StartupInfo.cb = Marshal.SizeOf<Native.STARTUPINFOEX>();
            // Null standard handles with STARTF_USESTDHANDLES: the child uses the pseudo console, not the handles Fyrd's
            // own process was given (which point at its log file when it runs detached).
            si.StartupInfo.dwFlags = Native.STARTF_USESTDHANDLES;
            var cmd = new StringBuilder(CommandLine(command));
            if (!Native.CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, false,
                    Native.EXTENDED_STARTUPINFO_PRESENT | Native.CREATE_SUSPENDED, IntPtr.Zero, workingDirectory, ref si, out pi))
                throw new OwnerMessageException($"Could not start claude (error {Marshal.GetLastWin32Error()}).");
            // Put it in the job before it runs, so nothing it starts escapes the job.
            if (!Native.AssignProcessToJobObject(job, pi.hProcess))
            {
                var error = Marshal.GetLastWin32Error();
                Native.TerminateProcess(pi.hProcess, 1);
                Native.CloseHandle(pi.hThread); Native.CloseHandle(pi.hProcess);
                throw new OwnerMessageException($"Could not put claude in its job (error {error}).");
            }
            Native.ResumeThread(pi.hThread);
        }
        catch
        {
            ClosePc(); inWrite.Dispose(); outRead.Dispose(); job.Dispose();
            throw;
        }
        finally
        {
            if (attrs != IntPtr.Zero) { Native.DeleteProcThreadAttributeList(attrs); Marshal.FreeHGlobal(attrs); }
        }

        using var jobHandle = job; // closing the job kills whatever is still running in it
        var process = new SafeProcessHandle(pi.hProcess, ownsHandle: true);
        Native.CloseHandle(pi.hThread);
        using var inputWriter = inWrite; // kept open (nothing is typed) so the console doesn't see end of input

        var output = new TerminalOutput(stop);
        var pump = Task.Run(() =>
        {
            using var stream = new FileStream(outRead, FileAccess.Read, 1);
            var decoder = Encoding.UTF8.GetDecoder();
            var bytes = new byte[4096];
            var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
            int n;
            try
            {
                while ((n = stream.Read(bytes, 0, bytes.Length)) > 0)
                {
                    var c = decoder.GetChars(bytes, 0, n, chars, 0);
                    if (c > 0) output.Append(new string(chars, 0, c));
                }
            }
            catch (IOException) { } // the pseudo console was closed
        });

        var exited = Task.Run(() =>
        {
            using var wait = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false) };
            wait.WaitOne();
        });
        // Once the command has exited, closing the pseudo console ends its output, so the pump finishes.
        _ = exited.ContinueWith(_ => ClosePc(), TaskScheduler.Default);

        try
        {
            return await TerminalWait.RunAsync(exited,
                () => Native.GetExitCodeProcess(process, out var code) ? (int)code : -1,
                () => { Native.TerminateJobObject(job, 1); ClosePc(); },
                output, pump, timeout, ct);
        }
        finally
        {
            ClosePc();
            // The process handle is only closed once nothing waits on it any more. The job is terminated first so the wait
            // ends; if it still hasn't, the handle is left open rather than closed under a blocked wait.
            if (!exited.IsCompleted) Native.TerminateJobObject(job, 1);
            try { await exited.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None); } catch (TimeoutException) { }
            if (exited.IsCompleted) process.Dispose();
        }
    }

    private static class Native
    {
        public const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        public const uint CREATE_SUSPENDED = 0x00000004;
        public const int STARTF_USESTDHANDLES = 0x00000100;
        public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
        public const int JobObjectExtendedLimitInformation = 9;

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount,
                OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass,
            ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint ResumeThread(IntPtr thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool TerminateProcess(IntPtr process, uint exitCode);
        public const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

        [StructLayout(LayoutKind.Sequential)]
        public struct COORD { public short X; public short Y; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct STARTUPINFO
        {
            public int cb;
            public string? lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, int size);

        [DllImport("kernel32.dll")]
        public static extern int CreatePseudoConsole(COORD size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr hpc);

        [DllImport("kernel32.dll")]
        public static extern void ClosePseudoConsole(IntPtr hpc);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value,
            IntPtr size, IntPtr previous, IntPtr returnSize);

        [DllImport("kernel32.dll")]
        public static extern void DeleteProcThreadAttributeList(IntPtr list);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateProcessW(string? application, StringBuilder commandLine, IntPtr processAttributes,
            IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string currentDirectory,
            ref STARTUPINFOEX startupInfo, out PROCESS_INFORMATION processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}

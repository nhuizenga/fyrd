// Spike: run a command inside a Windows pseudo-console (ConPTY) and capture its output.
// Usage: PtyProbe <timeoutSeconds> <command line...>
// Lines on stdin of the form "SEND:<text>" are typed into the console; "\r" is appended.
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

var timeout = TimeSpan.FromSeconds(int.Parse(args[0]));
var commandLine = string.Join(' ', args.Skip(1));

if (!Native.CreatePipe(out var inRead, out var inWrite, IntPtr.Zero, 0) ||
    !Native.CreatePipe(out var outRead, out var outWrite, IntPtr.Zero, 0))
    throw new Win32Exception();

var hr = Native.CreatePseudoConsole(new Native.COORD { X = 200, Y = 50 }, inRead, outWrite, 0, out var hPC);
if (hr != 0) throw new Win32Exception(hr);

var size = IntPtr.Zero;
Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
var attrList = Marshal.AllocHGlobal(size);
if (!Native.InitializeProcThreadAttributeList(attrList, 1, 0, ref size)) throw new Win32Exception();
if (!Native.UpdateProcThreadAttribute(attrList, 0, (IntPtr)0x00020016, hPC, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
    throw new Win32Exception();

var si = new Native.STARTUPINFOEX();
si.StartupInfo.cb = Marshal.SizeOf<Native.STARTUPINFOEX>();
si.lpAttributeList = attrList;
si.StartupInfo.dwFlags = 0x00000100; // STARTF_USESTDHANDLES with null handles: don't inherit our redirected stdio
if (!Native.CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, 0x00080000, IntPtr.Zero, Environment.CurrentDirectory, ref si, out var pi))
    throw new Win32Exception();

inRead.Dispose();
outWrite.Dispose();

var input = new FileStream(inWrite, FileAccess.Write);
var output = new FileStream(outRead, FileAccess.Read);

var reader = Task.Run(async () =>
{
    var buf = new byte[8192];
    int n;
    while ((n = await output.ReadAsync(buf)) > 0)
    {
        var text = Encoding.UTF8.GetString(buf, 0, n);
        // Answer cursor-position queries so TUI apps don't stall.
        if (text.Contains("\x1b[6n")) { await input.WriteAsync(Encoding.ASCII.GetBytes("\x1b[1;1R")); await input.FlushAsync(); }
        Console.Out.Write(text);
        Console.Out.Flush();
    }
});

_ = Task.Run(async () =>
{
    string? line;
    while ((line = await Console.In.ReadLineAsync()) != null)
    {
        if (!line.StartsWith("SEND:")) continue;
        await input.WriteAsync(Encoding.UTF8.GetBytes(line[5..] + "\r"));
        await input.FlushAsync();
    }
});

var exited = Native.WaitForSingleObject(pi.hProcess, (uint)timeout.TotalMilliseconds) == 0;
Console.Error.WriteLine(exited ? "\n[probe] process exited" : "\n[probe] timeout; closing pseudo-console");
Native.ClosePseudoConsole(hPC);
await Task.WhenAny(reader, Task.Delay(2000));

static class Native
{
    [StructLayout(LayoutKind.Sequential)] public struct COORD { public short X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] public struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }
    [StructLayout(LayoutKind.Sequential)] public struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);
    [DllImport("kernel32.dll")]
    public static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);
    [DllImport("kernel32.dll")] public static extern void ClosePseudoConsole(IntPtr hPC);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CreateProcess(string? lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);
    [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);
}

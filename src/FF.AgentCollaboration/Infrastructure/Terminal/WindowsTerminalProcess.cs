using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Windows;

namespace FF.AgentCollaboration.Infrastructure.Terminal;

internal sealed class WindowsTerminalProcess : ITerminalProcess
{
    private readonly FileStream input;
    private readonly FileStream output;
    private readonly SafeProcessHandle process;
    private readonly TerminalJob job;
    private readonly nint console;

    internal WindowsTerminalProcess(string executable, IReadOnlyList<string> arguments, string cwd, string grant,
        int cols, int rows, IReadOnlyDictionary<string, string>? environment)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            throw new WorkbenchError("真终端需要支持 ConPTY 的 Windows");
        if (!Native.CreatePipe(out var consoleInput, out var hostInput, 0, 0))
            throw Native.Error("无法创建终端输入管道");
        using var inputSide = consoleInput;
        if (!Native.CreatePipe(out var hostOutput, out var consoleOutput, 0, 0))
        {
            hostInput.Dispose();
            throw Native.Error("无法创建终端输出管道");
        }
        using var outputSide = consoleOutput;
        try
        {
            job = new TerminalJob();
            Marshal.ThrowExceptionForHR(Native.CreatePseudoConsole(new((short)cols, (short)rows), consoleInput, consoleOutput, 0, out console));
            input = new FileStream(hostInput, FileAccess.Write, bufferSize: 1);
            output = new FileStream(hostOutput, FileAccess.Read, bufferSize: 4096);
            process = Launch(executable, arguments, cwd, grant, environment);
        }
        catch
        {
            job?.Dispose();
            inputSide.Dispose();
            outputSide.Dispose();
            if (console != 0) Native.ClosePseudoConsole(console);
            input?.Dispose();
            output?.Dispose();
            hostInput.Dispose();
            hostOutput.Dispose();
            throw;
        }
    }

    public Stream Input => input;
    public Stream Output => output;
    public uint? ExitCode => Native.WaitForSingleObject(process, 0) == 0 &&
        Native.GetExitCodeProcess(process, out uint code) ? code : null;

    public async Task WaitForExitAsync(CancellationToken ct)
    {
        bool held = false;
        try
        {
            process.DangerousAddRef(ref held);
            using var signal = new EventWaitHandle(false, EventResetMode.ManualReset) {
                SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false)
            };
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var wait = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) => exited.TrySetResult(), null, Timeout.Infinite, true);
            try { await exited.Task.WaitAsync(ct); }
            finally { wait.Unregister(null); }
        }
        finally { if (held) process.DangerousRelease(); }
    }

    public void Resize(int cols, int rows) =>
        Marshal.ThrowExceptionForHR(Native.ResizePseudoConsole(console, new((short)cols, (short)rows)));

    public void Stop()
    {
        Native.ClosePseudoConsole(console);
        Native.WaitForSingleObject(process, 2000);
        job.TerminateRemaining();
    }

    public void Dispose()
    {
        input.Dispose();
        output.Dispose();
        process.Dispose();
        job.Dispose();
    }

    private SafeProcessHandle Launch(string executable, IReadOnlyList<string> arguments, string cwd, string grant,
        IReadOnlyDictionary<string, string>? overrides)
    {
        nint size = 0;
        Native.InitializeProcThreadAttributeList(0, 2, 0, ref size);
        nint attributes = Marshal.AllocHGlobal(size + IntPtr.Size);
        nint jobList = attributes + size;
        bool initialized = false;
        nint environment = 0;
        try
        {
            if (!Native.InitializeProcThreadAttributeList(attributes, 2, 0, ref size))
                throw Native.Error("无法初始化终端进程属性");
            initialized = true;
            if (!Native.UpdateProcThreadAttribute(attributes, 0, 0x00020016, console, IntPtr.Size, 0, 0))
                throw Native.Error("无法将进程连接到 ConPTY");
            // PROC_THREAD_ATTRIBUTE_JOB_LIST 在创建进程时生效，消除创建与加入 Job 之间宿主退出的窗口。
            Marshal.WriteIntPtr(jobList, job.Handle.DangerousGetHandle());
            if (!Native.UpdateProcThreadAttribute(attributes, 0, 0x0002000D, jobList, IntPtr.Size, 0, 0))
                throw Native.Error("无法将进程绑定到窗格");

            var start = WindowsCommand.Resolve(executable, arguments);
            var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in start.Environment)
                if (pair.Value != null) variables[pair.Key] = pair.Value;
            if (overrides != null)
                foreach (var pair in overrides) variables[pair.Key] = pair.Value;
            variables["FF_P0_WINDOW_GRANT"] = variables["LX_P0_WINDOW_GRANT"] = grant;
            variables["TERM"] = "xterm-256color";
            environment = Marshal.StringToHGlobalUni(string.Join('\0', variables.Select(pair => $"{pair.Key}={pair.Value}")) + "\0\0");
            var startup = new Native.StartupInfoEx
            {
                // Windows 10 宿主重定向标准流时，显式传空句柄让 ConPTY 分配控制台标准流。
                Info = new Native.StartupInfo { Size = Marshal.SizeOf<Native.StartupInfoEx>(), Flags = 0x00000100 },
                AttributeList = attributes
            };
            executable = start.FileName;
            var command = new StringBuilder(WindowsCommand.QuoteArgument(executable) + " " + start.Arguments);
            if (!Native.CreateProcess(executable, command, 0, 0, false, 0x00080000 | 0x00000400,
                environment, cwd, ref startup, out var handles))
                throw Native.Error("无法启动原生 CLI");
            using var initialThread = new SafeFileHandle(handles.Thread, ownsHandle: true);
            return new SafeProcessHandle(handles.Process, ownsHandle: true);
        }
        finally
        {
            if (initialized) Native.DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes);
            if (environment != 0) Marshal.FreeHGlobal(environment);
        }
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal readonly record struct Coord(short X, short Y);

        [StructLayout(LayoutKind.Sequential)]
        internal struct StartupInfo
        {
            internal int Size;
            internal nint Reserved, Desktop, Title;
            internal int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
            internal ushort ShowWindow, ReservedLength;
            internal nint ReservedBytes, Input, Output, Error;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct StartupInfoEx
        {
            internal StartupInfo Info;
            internal nint AttributeList;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct ProcessInfo
        {
            internal nint Process, Thread;
            internal int ProcessId, ThreadId;
        }

        internal static Exception Error(string action) =>
            new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), action);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, nint attributes, int size);
        // 优先使用随程序分发的 Microsoft.Windows.Console.ConPTY（conpty.dll + OpenConsole.exe），
        // 绕开 Windows 10 自带 conhost.exe 在长中文粘贴时的 0xc0000409 崩溃。
        [DllImport("conpty.dll", EntryPoint = "ConptyCreatePseudoConsole")]
        internal static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out nint console);
        [DllImport("conpty.dll", EntryPoint = "ConptyResizePseudoConsole")]
        internal static extern int ResizePseudoConsole(nint console, Coord size);
        [DllImport("conpty.dll", EntryPoint = "ConptyClosePseudoConsole")]
        internal static extern void ClosePseudoConsole(nint console);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nint size, nint previous, nint returned);
        [DllImport("kernel32.dll")]
        internal static extern void DeleteProcThreadAttributeList(nint list);
        [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool CreateProcess(string application, StringBuilder command, nint processAttributes, nint threadAttributes,
            bool inheritHandles, uint flags, nint environment, string directory, ref StartupInfoEx startup, out ProcessInfo process);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
    }
}

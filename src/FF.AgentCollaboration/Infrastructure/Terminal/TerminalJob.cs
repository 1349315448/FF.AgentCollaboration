using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using FF.AgentCollaboration.Domain;

namespace FF.AgentCollaboration.Infrastructure.Terminal;

// 每个窗格独占一个不继承的句柄。即使宿主被强制退出，Windows 也会回收此窗格的整个进程树。
internal sealed class TerminalJob : IDisposable
{
    internal SafeFileHandle Handle { get; }

    internal TerminalJob()
    {
        Handle = CreateJobObjectW(0, null);
        if (Handle.IsInvalid) throw new Win32Exception();
        const uint killOnJobClose = 0x2000;
        var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = killOnJobClose } };
        if (!SetInformationJobObject(Handle, 9, ref limits, Marshal.SizeOf<ExtendedLimits>()))
        {
            var error = new Win32Exception();
            Handle.Dispose();
            throw error;
        }
    }

    internal void TerminateRemaining()
    {
        if (!TerminateJobObject(Handle, 1)) throw new Win32Exception();
        // 脱离控制台的子进程也可能持有会话锁。确认全部退出后才允许下一次接管。
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            if (!QueryInformationJobObject(Handle, 1, out var usage, Marshal.SizeOf<Accounting>(), 0))
                throw new Win32Exception();
            if (usage.ActiveProcesses == 0) return;
            if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(5))
                throw new WorkbenchError("窗格进程仍在退出，请稍后重试关闭", WorkbenchFailure.Conflict);
            Thread.Sleep(10);
        }
    }

    public void Dispose() => Handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint LimitFlags;
        public nuint MinWorkingSet, MaxWorkingSet;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint Priority, Scheduling;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Accounting
    {
        public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime;
        public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimits info, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int kind, out Accounting info, int size, nint returned);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
}

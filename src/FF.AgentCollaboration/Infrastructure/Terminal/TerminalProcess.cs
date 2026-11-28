namespace FF.AgentCollaboration.Infrastructure.Terminal;

// 会话层共享 VT 字节、草稿与投递逻辑。操作系统只负责真终端、尺寸和子进程生命周期。
internal interface ITerminalProcess : IDisposable
{
    Stream Input { get; }
    Stream Output { get; }
    uint? ExitCode { get; }
    Task WaitForExitAsync(CancellationToken ct);
    void Resize(int cols, int rows);
    void Stop();
}

internal static class TerminalProcess
{
    internal static ITerminalProcess Start(string executable, IReadOnlyList<string> arguments, string cwd, string grant,
        int cols, int rows, IReadOnlyDictionary<string, string>? environment)
    {
        if (OperatingSystem.IsWindows())
            return new WindowsTerminalProcess(executable, arguments, cwd, grant, cols, rows, environment);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            return new UnixTerminalProcess(executable, arguments, cwd, grant, cols, rows, environment);
        throw new PlatformNotSupportedException("此系统尚未适配原生终端");
    }
}

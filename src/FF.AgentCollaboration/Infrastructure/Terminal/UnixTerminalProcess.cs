using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Processes;

namespace FF.AgentCollaboration.Infrastructure.Terminal;

internal sealed class UnixTerminalProcess : ITerminalProcess
{
    internal static string Helper => Path.Combine(AppContext.BaseDirectory, "FF.AgentCollaboration.Pty");
    private readonly Process bridge;
    private readonly CommandStream input;

    internal UnixTerminalProcess(string executable, IReadOnlyList<string> arguments, string cwd, string grant,
        int cols, int rows, IReadOnlyDictionary<string,string>? environment)
    {
        if (!HostCommand.IsUnixExecutable(Helper))
            throw new WorkbenchError("缺少可执行的 FF.AgentCollaboration.Pty，请使用对应平台的完整工作台目录");
        var start = new ProcessStartInfo(Helper) { WorkingDirectory = cwd, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false) };
        var cli = HostCommand.Resolve(executable, arguments);
        start.ArgumentList.Add(cols.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(rows.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(cli.FileName);
        foreach (string arg in cli.ArgumentList) start.ArgumentList.Add(arg);
        if (environment != null)
            foreach (var (key,value) in environment) start.Environment[key] = value;
        start.Environment["TERM"] = "xterm-256color";
        start.Environment["FF_P0_WINDOW_GRANT"] = start.Environment["LX_P0_WINDOW_GRANT"] = grant;
        bridge = Process.Start(start) ?? throw new WorkbenchError("无法启动 Unix PTY");
        try
        {
            // 子进程获得控制终端之后才确认上线。初始化回执与 CLI 的输出分开传输。
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var ready = bridge.StandardError.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult();
            if (ready != "READY") throw new WorkbenchError("无法启动 CLI：" + (ready ?? "PTY 已退出"));
            input = new CommandStream(bridge.StandardInput.BaseStream);
        }
        catch
        {
            try { Stop(); } finally { bridge.Dispose(); }
            throw;
        }
    }

    public Stream Input => input;
    public Stream Output => bridge.StandardOutput.BaseStream;
    public uint? ExitCode => bridge.HasExited ? unchecked((uint)bridge.ExitCode) : null;
    public Task WaitForExitAsync(CancellationToken ct) => bridge.WaitForExitAsync(ct);
    public void Resize(int cols, int rows) => input.Resize(cols, rows);
    public void Stop()
    {
        bridge.StandardInput.Close();
        if (bridge.WaitForExit(3000)) return;
        // 当 CLI 不再读取 PTY 时，控制管道可能尚未读到 EOF。辅助进程也监听 SIGTERM。
        Signal(bridge.Id,15);
        if (!bridge.WaitForExit(3000))
        {
            bridge.Kill(entireProcessTree:true);
            bridge.WaitForExit();
        }
    }
    public void Dispose() => bridge.Dispose();

    [DllImport("libc", EntryPoint="kill")]
    private static extern int Signal(int pid, int signal);

    // 单管道中的键盘数据与 resize 命令必须保持帧的原子性；stdout 始终是原始 PTY 数据。
    private sealed class CommandStream(Stream pipe) : Stream
    {
        private readonly object writer = new();
        private void Send(byte type, ReadOnlySpan<byte> bytes)
        {
            lock (writer)
            {
                Span<byte> header = stackalloc byte[5];
                header[0] = type;
                BinaryPrimitives.WriteInt32BigEndian(header[1..], bytes.Length);
                pipe.Write(header);
                pipe.Write(bytes);
                pipe.Flush();
            }
        }
        internal void Resize(int cols, int rows)
        {
            Span<byte> size = stackalloc byte[4];
            BinaryPrimitives.WriteUInt16BigEndian(size, checked((ushort)cols));
            BinaryPrimitives.WriteUInt16BigEndian(size[2..], checked((ushort)rows));
            Send(2, size);
        }
        public override void Write(byte[] bytes, int offset, int count) => Send(1, bytes.AsSpan(offset,count));
        public override void Write(ReadOnlySpan<byte> bytes) => Send(1,bytes);
        public override void Flush() { }
        public override bool CanWrite => true;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

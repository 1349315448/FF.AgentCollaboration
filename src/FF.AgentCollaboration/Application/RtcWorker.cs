using System.Diagnostics;
using System.Text.Json;

namespace FF.AgentCollaboration.Application;

/// <summary>浏览器独立的传输进程。标准输入仅交换 SDP 和启动参数。</summary>
internal sealed class RtcWorker : IDisposable
{
    private readonly Process process;
    private int disposed;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    internal RtcWorker(string executable)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        process = Process.Start(start) ?? throw new IOException("无法启动 WebRTC 传输进程");
        process.BeginErrorReadLine();
    }

    internal bool Online => Volatile.Read(ref disposed) == 0 && !process.HasExited;
    internal int Port { get; private set; }

    internal async Task Write(object data, CancellationToken ct)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(data, Web).AsMemory(), ct);
        await process.StandardInput.FlushAsync(ct);
    }

    internal async Task<JsonElement> Read(CancellationToken ct)
    {
        var line = await process.StandardOutput.ReadLineAsync(ct);
        if (line is null || line.Length > 40_000) throw new IOException("WebRTC 传输进程没有返回有效结果");
        using var json = JsonDocument.Parse(line);
        return json.RootElement.Clone();
    }

    internal async Task Ready(CancellationToken ct)
    {
        var ready = await Read(ct);
        if (ready.TryGetProperty("port", out var port) && port.TryGetInt32(out var value) && value is > 0 and <= 65535)
            Port = value;
        else if (!ready.TryGetProperty("ready", out var flag) || flag.ValueKind != JsonValueKind.True)
            throw new IOException("WebRTC 传输进程未就绪");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        finally { process.Dispose(); }
    }
}

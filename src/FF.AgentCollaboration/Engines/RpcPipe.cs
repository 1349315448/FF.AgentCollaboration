using FF.AgentCollaboration.Infrastructure.Processes;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FF.AgentCollaboration.Engines;

// 每个窗格串行执行原生轮次；等待响应期间同步处理反向请求与流式通知。
internal sealed class RpcPipe : IDisposable
{
    private readonly Process child;
    private readonly bool contentLength;
    private readonly bool bareEnvelope;
    private int nextId;
    internal Action<string, JsonNode?> Notice { get; set; } = (_, _) => { };
    internal RpcPipe(string executable, IEnumerable<string> args, string cwd, string? grant, bool contentLength = false,
        bool bareEnvelope = false)
    {
        this.contentLength = contentLength;
        this.bareEnvelope = bareEnvelope;
        var start = HostCommand.Resolve(executable, args.ToArray());
        start.WorkingDirectory = cwd;
        start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
        start.StandardInputEncoding = new UTF8Encoding(false);
        start.StandardOutputEncoding = Encoding.UTF8;
        if (grant != null) start.Environment["FF_P0_WINDOW_GRANT"] = start.Environment["LX_P0_WINDOW_GRANT"] = grant;
        child = Process.Start(start) ?? throw new InvalidOperationException("无法启动引擎");
        // 引擎诊断可能含账号或握手参数，只消费 stderr，协议错误通过 RPC 返回。
        child.BeginErrorReadLine();
    }

    private async Task Write(object payload, CancellationToken ct)
    {
        if (contentLength)
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(payload);
            var stream = child.StandardInput.BaseStream;
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"), ct);
            await stream.WriteAsync(body, ct);
            await stream.FlushAsync(ct);
            return;
        }
        string line;
        if (bareEnvelope)
        {
            var envelope = JsonSerializer.SerializeToNode(payload)!.AsObject();
            envelope.Remove("jsonrpc");
            line = envelope.ToJsonString();
        }
        else line = JsonSerializer.Serialize(payload);
        await child.StandardInput.WriteLineAsync(line.AsMemory(), ct);
        await child.StandardInput.FlushAsync(ct);
    }
    internal Task Notify(string method, CancellationToken ct) => Write(new { jsonrpc = "2.0", method }, ct);
    internal async Task<JsonNode> Request(string method, object parameters, CancellationToken ct)
    {
        int id = ++nextId;
        await Write(new { jsonrpc = "2.0", id, method, @params = parameters }, ct);
        while (true)
        {
            var response = await Read(ct);
            if (response["method"] == null && (int?)response["id"] == id)
            {
                if (response["error"] is { } err)
                    throw new EngineRpcError(method, (int?)err["code"], (string?)err["message"] ?? "未知协议错误");
                return response["result"] ?? new JsonObject();
            }
        }
    }
    internal async Task<JsonNode> Read(CancellationToken ct)
    {
        JsonNode packet;
        if (contentLength) packet = await ReadFrame(ct);
        else
        {
            string? line = await child.StandardOutput.ReadLineAsync(ct);
            if (line == null) throw new EndOfStreamException("引擎协议管道已关闭");
            packet = JsonNode.Parse(line)!;
        }
        if (packet["method"] is not { } methodNode) return packet;
        var method = (string)methodNode!;
        if (packet["id"] is { } id)
        {
            object? decision = method switch
            {
                "session/request_permission" => new { outcome = new { outcome = "cancelled" } },
                "item/commandExecution/requestApproval" or "item/fileChange/requestApproval" => new { decision = "decline" },
                "item/permissions/requestApproval" => new { permissions = new { }, scope = "turn" },
                _ => null
            };
            if (decision == null)
                await Write(new { jsonrpc = "2.0", id, error = new { code = -32601, message = "Unsupported client request" } }, ct);
            else await Write(new { jsonrpc = "2.0", id, result = decision }, ct);
        }
        else Notice(method, packet["params"]);
        return packet;
    }

    // Copilot SDK 的只读模型 RPC 使用按 UTF-8 字节计长的帧。
    private async Task<JsonNode> ReadFrame(CancellationToken ct)
    {
        var stream = child.StandardOutput.BaseStream;
        var header = new byte[1024];
        int size = 0;
        do
        {
            if (size == header.Length) throw new InvalidDataException("引擎返回了过长的 RPC 帧头");
            await stream.ReadExactlyAsync(header.AsMemory(size++, 1), ct);
        }
        while (size < 4 || !header.AsSpan(size - 4, 4).SequenceEqual("\r\n\r\n"u8));

        const string prefix = "Content-Length:";
        var length = Encoding.ASCII.GetString(header, 0, size).Split("\r\n")
            .FirstOrDefault(line => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (length == null || !int.TryParse(length.AsSpan(prefix.Length), out int bytes) || bytes is <= 0 or > 4_194_304)
            throw new InvalidDataException("引擎返回了无效的 RPC 帧长度");
        var body = new byte[bytes];
        await stream.ReadExactlyAsync(body, ct);
        return JsonNode.Parse(body)!;
    }
    internal void Abort()
    {
        try { if (!child.HasExited) child.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) when (child.HasExited) { }
        child.WaitForExit();
    }
    public void Dispose()
    {
        if (!child.HasExited)
        {
            child.StandardInput.Close();
            if (!child.WaitForExit(2000)) Abort();
        }
        child.Dispose();
    }
}

internal sealed class EngineRpcError(string method, int? code, string message) : Exception($"{method}: {message}")
{
    internal int? Code { get; } = code;
    internal string RemoteMessage { get; } = message;
}

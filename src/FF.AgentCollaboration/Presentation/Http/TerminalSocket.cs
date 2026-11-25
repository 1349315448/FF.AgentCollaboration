using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Terminal;

namespace FF.AgentCollaboration.Presentation.Http;

// [01] 二进制消息承载 VT 字节。八字节序号用于确认浏览器已渲染的那次输出。
internal static class TerminalSocket
{
    internal static async Task RunLogin(HttpContext context, TerminalSession terminal, CancellationToken stopping)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        using var signal = terminal.ExitSignal();
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wait = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) => exited.TrySetResult(), null, Timeout.Infinite, true);
        try
        {
            var connection = Run(context, terminal, lifetime.Token);
            if (await Task.WhenAny(connection, exited.Task) == exited.Task) await lifetime.CancelAsync();
            await connection;
        }
        finally { wait.Unregister(null); }
    }

    internal static async Task Run(HttpContext context, TerminalSession terminal, CancellationToken stopping)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping);
        var viewer = terminal.Subscribe();
        try
        {
            var sender = Send(socket, viewer, stop.Token);
            var receiver = Receive(socket, terminal, viewer.Id, stop.Token);
            await Task.WhenAny(sender, receiver);
            await stop.CancelAsync();
            await Task.WhenAll(sender, receiver);
        }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException or IOException or JsonException or WorkbenchError)
        {
            // 关闭或过慢的浏览器连接不影响 ConPTY 进程。
        }
        finally { terminal.Detach(viewer.Id); }
        if (!stopping.IsCancellationRequested && (socket.State is WebSocketState.Open or WebSocketState.CloseReceived))
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Terminal stream closed", context.RequestAborted);
    }

    private static async Task Send(WebSocket socket, TerminalViewer viewer, CancellationToken ct)
    {
        var header = new byte[8];
        async ValueTask Frame(TerminalFrame frame)
        {
            if (frame.MentionRequest != 0)
            {
                var ack = JsonSerializer.SerializeToUtf8Bytes(new { type = "mention-ready", request = frame.MentionRequest });
                await socket.SendAsync(ack, WebSocketMessageType.Text, true, ct);
                return;
            }
            BinaryPrimitives.WriteInt64LittleEndian(header, frame.Revision);
            await socket.SendAsync(header.AsMemory(), WebSocketMessageType.Binary, false, ct);
            await socket.SendAsync(frame.Data.AsMemory(), WebSocketMessageType.Binary, true, ct);
        }
        // 浏览器必须把历史 VT 查询和当前 CLI 查询区分开，重放的响应不能回送原生输入。
        var boundary = JsonSerializer.SerializeToUtf8Bytes(new
        {
            replayThrough = viewer.Replay.LastOrDefault()?.Revision ?? 0,
            cols = viewer.Columns,
            rows = viewer.Rows
        });
        await socket.SendAsync(boundary.AsMemory(), WebSocketMessageType.Text, true, ct);
        foreach (var frame in viewer.Replay) await Frame(frame);
        await foreach (var frame in viewer.Live.ReadAllAsync(ct)) await Frame(frame);
    }

    private static async Task Receive(WebSocket socket, TerminalSession terminal, int viewerId, CancellationToken ct)
    {
        var buffer = new byte[8192];
        while (!ct.IsCancellationRequested)
        {
            using var packet = new MemoryStream();
            ValueWebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer.AsMemory(), ct);
                if (result.MessageType == WebSocketMessageType.Close) return;
                packet.Write(buffer, 0, result.Count);
                if (packet.Length > 256_000) throw new WorkbenchError("终端输入过长");
            }
            while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                if (packet.Length != 0) await terminal.Input(packet.ToArray(), ct);
                continue;
            }
            packet.Position = 0;
            using var json = JsonDocument.Parse(packet);
            var command = json.RootElement;
            if (!command.TryGetProperty("type", out var type)) continue;
            switch (type.GetString())
            {
                case "mention-picker":
                    terminal.SetMentionPicker(viewerId, command.GetProperty("open").GetBoolean(),
                        command.TryGetProperty("request", out var token) && token.TryGetInt32(out int sequence) ? sequence : 0);
                    break;
                case "resize":
                    terminal.Resize(Math.Clamp(command.GetProperty("cols").GetInt32(), 12, 500),
                        Math.Clamp(command.GetProperty("rows").GetInt32(), 5, 250));
                    break;
                case "prompt":
                    terminal.ObservePrompt(viewerId, command.GetProperty("revision").GetInt64(), command.GetProperty("empty").GetBoolean(),
                        command.TryGetProperty("bracketedPaste", out var paste) && paste.GetBoolean(),
                        command.TryGetProperty("clearedDraft", out var cleared) && cleared.TryGetInt64(out var draft) ? draft : null,
                        command.TryGetProperty("retryDraft", out var candidate) ? candidate.GetString() : null,
                        command.TryGetProperty("screen", out var screen) ? screen.GetString() : null,
                        command.TryGetProperty("draftId", out var draftId) ? draftId.GetString() : null,
                        command.TryGetProperty("approvalDraft", out var approvalDraft) ? approvalDraft.GetString() : null);
                    break;
            }
        }
    }
}

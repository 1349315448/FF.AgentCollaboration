using System.Net.WebSockets;
using System.Text.Json;

namespace FF.AgentCollaboration.Signaling;

internal static class SignalingSocket
{
    private const int MaxMessageBytes = 32 * 1024;

    internal static string? Bearer(HttpRequest request)
    {
        var header = request.Headers.Authorization;
        const string prefix = "Bearer ";
        if (header.Count != 1 || header[0] is not { } value || !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        string token = value[prefix.Length..];
        return InvitationRooms.IsToken(token) ? token : null;
    }

    internal static async Task Handle(HttpContext context, InvitationRooms rooms, Guid id, CancellationToken stopping)
    {
        // 连接凭证由工作台后台发送，避免网页跨站握手。
        if (!context.WebSockets.IsWebSocketRequest || context.Request.Headers.ContainsKey("Origin"))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        var (peer, busy) = rooms.Connect(id, Bearer(context.Request));
        if (peer is null)
        {
            context.Response.StatusCode = busy ? StatusCodes.Status409Conflict : StatusCodes.Status401Unauthorized;
            return;
        }
        try
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping);
            var remaining = rooms.TimeRemaining(peer);
            lifetime.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
            using var writer = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            var read = Read(socket, rooms, peer, lifetime.Token);
            var write = Write(socket, peer, writer.Token);
            try
            {
                if (await Task.WhenAny(read, write) == read)
                {
                    var close = await read;
                    await writer.CancelAsync();
                    await Finish(write);
                    using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await socket.CloseOutputAsync(close, null, grace.Token);
                }
            }
            finally
            {
                await lifetime.CancelAsync();
                await Finish(write);
                await Finish(read);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException) { }
        finally { rooms.Leave(peer); }
    }

    private static async Task<WebSocketCloseStatus> Read(WebSocket socket, InvitationRooms rooms,
        InvitationRooms.Peer peer, CancellationToken token)
    {
        var buffer = new byte[MaxMessageBytes];
        while (true)
        {
            int count = 0;
            ValueWebSocketReceiveResult part;
            do
            {
                part = await socket.ReceiveAsync(buffer.AsMemory(count), token);
                if (part.MessageType == WebSocketMessageType.Close) return WebSocketCloseStatus.NormalClosure;
                if (part.MessageType != WebSocketMessageType.Text) return WebSocketCloseStatus.InvalidMessageType;
                count += part.Count;
                if (!part.EndOfMessage && count == buffer.Length) return WebSocketCloseStatus.MessageTooBig;
            } while (!part.EndOfMessage);

            if (!Allowed(buffer.AsMemory(0, count), peer.Host)) return WebSocketCloseStatus.PolicyViolation;
            rooms.Relay(peer, buffer[..count]);
        }
    }

    private static bool Allowed(ReadOnlyMemory<byte> payload, bool host)
    {
        try
        {
            using var message = JsonDocument.Parse(payload);
            var root = message.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String) return false;
            return type.GetString() switch
            {
                "offer" when host => HasSdp(root),
                "answer" when !host => HasSdp(root),
                "ice" => root.TryGetProperty("candidate", out var candidate) && candidate.ValueKind == JsonValueKind.Object &&
                    candidate.TryGetProperty("candidate", out var line) && line.ValueKind == JsonValueKind.String &&
                    line.GetString()!.StartsWith("candidate:", StringComparison.Ordinal),
                "ice-complete" => true,
                _ => false
            };
        }
        catch (JsonException) { return false; }
    }

    private static bool HasSdp(JsonElement root) =>
        root.TryGetProperty("sdp", out var sdp) && sdp.ValueKind == JsonValueKind.String &&
        sdp.GetString()!.StartsWith("v=0", StringComparison.Ordinal);

    private static async Task Write(WebSocket socket, InvitationRooms.Peer peer, CancellationToken token)
    {
        await foreach (byte[] frame in peer.Frames.Reader.ReadAllAsync(token))
            await socket.SendAsync(frame.AsMemory(), WebSocketMessageType.Text, true, token);
    }

    private static async Task Finish(Task task)
    {
        try { await task; }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException) { }
    }
}

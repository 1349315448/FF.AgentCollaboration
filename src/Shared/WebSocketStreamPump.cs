using System.Net.WebSockets;

namespace FF.AgentCollaboration.Transport;

internal static class WebSocketStreamPump
{
    // Backpressure comes from the stream writes. Each direction owns one buffer.
    internal static async Task Forward(WebSocket socket, Stream stream, CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var send = Send();
        var receive = Receive();
        await Task.WhenAny(send, receive);
        await lifetime.CancelAsync();
        socket.Abort();
        await Task.WhenAll(send, receive);

        async Task Send()
        {
            var buffer = new byte[16 * 1024];
            int count;
            while ((count = await stream.ReadAsync(buffer, lifetime.Token)) > 0)
                await socket.SendAsync(buffer.AsMemory(0, count), WebSocketMessageType.Binary, true, lifetime.Token);
        }

        async Task Receive()
        {
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var part = await socket.ReceiveAsync(buffer.AsMemory(), lifetime.Token);
                if (part.MessageType == WebSocketMessageType.Close) return;
                if (part.MessageType != WebSocketMessageType.Binary) throw new IOException("Invalid relay frame");
                await stream.WriteAsync(buffer.AsMemory(0, part.Count), lifetime.Token);
            }
        }
    }
}

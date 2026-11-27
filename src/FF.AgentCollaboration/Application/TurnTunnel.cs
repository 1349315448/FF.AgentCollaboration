using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using FF.AgentCollaboration.Transport;

namespace FF.AgentCollaboration.Application;

// The ICE library speaks standard TURN/TCP on loopback. The authenticated WSS
// tunnel carries it to the room through the same public port as signaling.
internal sealed class TurnTunnel : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource closed = new();
    internal int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    internal TurnTunnel(SignalingTrust trust, string room, string ticket)
    {
        listener.Start(1);
        _ = Serve(trust, room, ticket, closed.Token);
    }

    private async Task Serve(SignalingTrust trust, string room, string ticket, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(ct);
                try
                {
                    using var websocket = await trust.Open(room, ticket, ct, relay: true);
                    await WebSocketStreamPump.Forward(websocket, client.GetStream(), ct);
                }
                catch (Exception e) when (e is WebSocketException or IOException or HttpRequestException) { }
            }
        }
        catch (Exception e) when (ct.IsCancellationRequested && e is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    public void Dispose()
    {
        closed.Cancel();
        listener.Stop();
        closed.Dispose();
    }
}

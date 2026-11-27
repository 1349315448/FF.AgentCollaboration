using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using FF.AgentCollaboration.Transport;

namespace FF.AgentCollaboration.Signaling;

internal sealed class TurnRoom
{
    internal string Password { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    private readonly Lock gate = new();
    private bool host, guest;
    private RtcWorker? worker;
    private Lazy<Task<int>>? port;
    internal bool Active { get { lock (gate) return host || guest; } }

    internal Lease? Acquire(bool asHost, string executable)
    {
        lock (gate)
        {
            if (asHost ? host : guest) return null;
            if (asHost) host = true;
            else guest = true;
            port ??= new Lazy<Task<int>>(() => Start(executable));
            return new Lease(this, asHost, port);
        }
    }

    private async Task<int> Start(string executable)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var process = worker = new RtcWorker(executable);
        await process.Write(new { role = "turn-server", password = Password }, timeout.Token);
        await process.Ready(timeout.Token);
        return process.Port;
    }

    internal sealed class Lease(TurnRoom room, bool host, Lazy<Task<int>> port) : IDisposable
    {
        internal Task<int> Port => port.Value;
        public void Dispose() => room.Release(host);
    }

    private void Release(bool asHost)
    {
        lock (gate)
        {
            if (asHost) host = false;
            else guest = false;
            if (host || guest) return;
            worker?.Dispose();
            worker = null;
            port = null;
        }
    }

    internal static async Task Handle(HttpContext context, InvitationRooms rooms, Guid id)
    {
        if (!context.WebSockets.IsWebSocketRequest || context.Request.Headers.ContainsKey("Origin"))
        {
            context.Response.StatusCode = 400;
            return;
        }
        var (lease, status) = rooms.OpenRelay(id, SignalingSocket.Bearer(context.Request));
        if (lease == null) { context.Response.StatusCode = status; return; }
        using (lease)
        using (var upstream = new TcpClient(AddressFamily.InterNetwork))
        {
            var ct = context.RequestAborted;
            try
            {
                await upstream.ConnectAsync(IPAddress.Loopback, await lease.Port, ct);
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                await WebSocketStreamPump.Forward(socket, upstream.GetStream(), ct);
            }
            catch (Exception e) when (e is WebSocketException or SocketException or IOException or OperationCanceledException
                or System.ComponentModel.Win32Exception)
            {
                if (!context.Response.HasStarted) context.Response.StatusCode = 503;
            }
        }
    }
}

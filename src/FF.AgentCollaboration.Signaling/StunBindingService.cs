using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace FF.AgentCollaboration.Signaling;

// RFC 8489 unauthenticated Binding discovery. This listener returns only the
// observed sender address and transaction ID, and never forwards UDP traffic.
internal sealed class StunBindingService(Uri origin, ILogger<StunBindingService> logger) : BackgroundService
{
    private const uint Cookie = 0x2112A442;
    internal volatile bool Available;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IPAddress.TryParse(origin.IdnHost.Trim('[', ']'), out var advertised);
        var bind = advertised != null && IPAddress.IsLoopback(advertised) ? advertised :
            advertised?.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;
        try
        {
            using var udp = new UdpClient(new IPEndPoint(bind, origin.Port));
            Available = true;
            while (!stoppingToken.IsCancellationRequested)
            {
                UdpReceiveResult packet;
                try { packet = await udp.ReceiveAsync(stoppingToken); }
                catch (SocketException error) when (error.SocketErrorCode == SocketError.ConnectionReset) { continue; }
                if (Binding(packet.Buffer, packet.RemoteEndPoint) is { } reply)
                    await udp.SendAsync(reply, packet.RemoteEndPoint, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (SocketException error) { logger.LogWarning("STUN UDP {Port} unavailable ({Error})", origin.Port, error.SocketErrorCode); }
        finally { Available = false; }
    }

    private static byte[]? Binding(ReadOnlySpan<byte> request, IPEndPoint peer)
    {
        if (request.Length < 20 || request.Length % 4 != 0 || BinaryPrimitives.ReadUInt16BigEndian(request) != 1 ||
            BinaryPrimitives.ReadUInt16BigEndian(request[2..]) != request.Length - 20 ||
            BinaryPrimitives.ReadUInt32BigEndian(request[4..]) != Cookie) return null;
        byte[] ip = peer.Address.GetAddressBytes();
        var reply = new byte[28 + ip.Length];
        request[..20].CopyTo(reply);
        BinaryPrimitives.WriteUInt16BigEndian(reply, 0x0101);
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(2), (ushort)(8 + ip.Length));
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(20), 0x0020);
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(22), (ushort)(4 + ip.Length));
        reply[25] = ip.Length == 4 ? (byte)1 : (byte)2;
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(26), (ushort)(peer.Port ^ (Cookie >> 16)));
        for (int i = 0; i < ip.Length; i++) reply[28 + i] = (byte)(ip[i] ^ request[4 + i]);
        return reply;
    }
}

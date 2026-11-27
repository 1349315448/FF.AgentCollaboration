using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using System.Text.Json;

namespace FF.AgentCollaboration.Signaling;

internal sealed record InvitationTarget(string? NodeId, string? WindowId, string? Pin);
internal sealed record Invitation(string RoomId, string Link, string HostTicket, DateTimeOffset ExpiresAt);
internal sealed record ClaimReceipt(string Ticket, DateTimeOffset ExpiresAt);

/// <summary>原子签发和认领邀请，管理各房间的连接与转发队列。</summary>
internal sealed class InvitationRooms(Uri origin, TimeProvider clock, string? turnExecutable, StunBindingService stun)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private const int Capacity = 128;
    private static readonly byte[] PeerOffline = """{"type":"peer-offline"}"""u8.ToArray();
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, Room> rooms = new();

    internal Invitation? Issue(InvitationTarget target)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            foreach (var entry in rooms.Where(entry => entry.Value.ExpiresAt <= now && !entry.Value.Relay.Active).ToArray())
                rooms.Remove(entry.Key);
            if (rooms.Count >= Capacity) return null;

            var id = Guid.NewGuid();
            string hostTicket = NewToken(), joinCode = NewToken();
            var room = new Room(now + Lifetime, Digest(hostTicket), Digest(joinCode));
            rooms.Add(id, room);
            var link = new UriBuilder("ffcp", origin.IdnHost, origin.IsDefaultPort ? -1 : origin.Port, $"/signal/{id:N}")
            {
                Query = $"node={target.NodeId}&window={target.WindowId}&pin={target.Pin!.ToLowerInvariant()}",
                Fragment = joinCode
            };
            return new(id.ToString("N"), link.Uri.AbsoluteUri, hostTicket, room.ExpiresAt);
        }
    }

    internal ClaimReceipt? Claim(Guid id, string? joinCode)
    {
        lock (gate)
        {
            var room = FindActive(id);
            if (room?.JoinHash is not { } expected || !Matches(expected, joinCode)) return null;
            string ticket = NewToken();
            room.JoinHash = null;
            room.GuestHash = Digest(ticket);
            return new(ticket, room.ExpiresAt);
        }
    }

    internal (Peer? Peer, bool Busy) Connect(Guid id, string? ticket)
    {
        lock (gate)
        {
            var room = FindActive(id);
            if (room is null) return (null, false);
            bool host = Matches(room.HostHash, ticket);
            if (!host && !Matches(room.GuestHash, ticket)) return (null, false);
            if (room.Member(host) is not null) return (null, true);
            var peer = new Peer(room, host);
            room.SetMember(host, peer);
            if (room.Member(!host) is { } other)
            {
                var ready = JsonSerializer.SerializeToUtf8Bytes(new { type = "ready", stun = stun.Available,
                    turnPassword = turnExecutable == null ? null : room.Relay.Password });
                Notify(peer, ready);
                Notify(other, ready);
            }
            return (peer, false);
        }
    }

    internal void Relay(Peer source, byte[] message)
    {
        lock (gate)
        {
            if (source.Room.Member(!source.Host) is { } receiver) Notify(receiver, message);
        }
    }

    internal void Leave(Peer peer)
    {
        lock (gate)
        {
            peer.Frames.Writer.TryComplete();
            peer.Room.SetMember(peer.Host, null);
            if (peer.Room.Member(!peer.Host) is { } other) Notify(other, PeerOffline);
        }
    }

    internal TimeSpan TimeRemaining(Peer peer) => peer.Room.ExpiresAt - clock.GetUtcNow();

    internal (TurnRoom.Lease? Lease, int Status) OpenRelay(Guid id, string? ticket)
    {
        lock (gate)
        {
            var room = FindActive(id);
            if (room?.GuestHash == null) return (null, 401);
            bool host = Matches(room.HostHash, ticket);
            if (!host && !Matches(room.GuestHash, ticket)) return (null, 401);
            if (turnExecutable == null) return (null, 503);
            var lease = room.Relay.Acquire(host, turnExecutable);
            return (lease, lease == null ? 409 : 200);
        }
    }

    private Room? FindActive(Guid id) =>
        rooms.TryGetValue(id, out var room) && room.ExpiresAt > clock.GetUtcNow() ? room : null;

    private static void Notify(Peer peer, byte[] message)
    {
        // 对端处理不过来时结束连接，让两端重连后重新协商，避免无限堆积。
        if (!peer.Frames.Writer.TryWrite(message)) peer.Frames.Writer.TryComplete();
    }

    private static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    internal static bool IsToken(string? token) => token?.Length == 64 && token.All(char.IsAsciiHexDigit);
    internal static byte[] Digest(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
    internal static bool Matches(byte[]? expected, string? token) => expected is not null && !string.IsNullOrEmpty(token) &&
        CryptographicOperations.FixedTimeEquals(expected, Digest(token));

    internal sealed class Room(DateTimeOffset expiresAt, byte[] hostHash, byte[] joinHash)
    {
        internal DateTimeOffset ExpiresAt { get; } = expiresAt;
        internal byte[] HostHash { get; } = hostHash;
        internal byte[]? JoinHash { get; set; } = joinHash;
        internal byte[]? GuestHash { get; set; }
        internal TurnRoom Relay { get; } = new();
        private Peer? host, guest;

        internal Peer? Member(bool asHost) => asHost ? host : guest;
        internal void SetMember(bool asHost, Peer? peer)
        {
            if (asHost) host = peer;
            else guest = peer;
        }
    }

    internal sealed class Peer(Room room, bool host)
    {
        internal Room Room { get; } = room;
        internal bool Host { get; } = host;
        internal Channel<byte[]> Frames { get; } = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(16)
        {
            SingleReader = true,
            SingleWriter = true
        });
    }
}

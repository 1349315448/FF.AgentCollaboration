using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using System.Text.Json;

namespace FF.AgentCollaboration.Signaling;

internal sealed record InvitationTarget(string? NodeId, string? WindowId, string? Pin);
internal sealed record Invitation(string RoomId, string Link, string HostTicket, DateTimeOffset ExpiresAt, string ResumeTicket);
internal sealed record ClaimReceipt(string Ticket, DateTimeOffset ExpiresAt, string? ResumeTicket = null);
internal sealed record ClaimRoom(string? NodeId);
internal sealed record PairRoom(string GuestTicket,string NodeId);
internal sealed record RoomPair(byte[] Host, byte[] Guest,string NodeId,bool? ClosedByHost=null,DateTimeOffset? ClosedAt=null);

/// <summary>原子签发和认领邀请，管理各房间的连接与转发队列。</summary>
internal sealed class InvitationRooms(Uri origin, TimeProvider clock, string? turnExecutable, StunBindingService stun, string pairingsPath)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private const int Capacity = 128;
    private static readonly byte[] PeerOffline = """{"type":"peer-offline"}"""u8.ToArray();
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, Room> rooms = new();
    private readonly Dictionary<Guid, RoomPair> pairs = LoadPairs(pairingsPath);

    internal Invitation? Issue(InvitationTarget target)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            foreach (var entry in rooms.Where(entry => entry.Value.LatestExpiry <= now && !entry.Value.Relay.Active).ToArray())
                rooms.Remove(entry.Key);
            if (rooms.Count >= Capacity) return null;

            var id = Guid.NewGuid();
            string hostTicket = NewToken(), joinCode = NewToken(), resumeTicket = NewToken();
            var room = new Room(now + Lifetime, Digest(hostTicket), Digest(joinCode)) { HostResume = Digest(resumeTicket) };
            rooms.Add(id, room);
            var link = new UriBuilder("ffcp", origin.IdnHost, origin.IsDefaultPort ? -1 : origin.Port, $"/signal/{id:N}")
            {
                Query = $"node={target.NodeId}&window={target.WindowId}&pin={target.Pin!.ToLowerInvariant()}",
                Fragment = joinCode
            };
            return new(id.ToString("N"), link.Uri.AbsoluteUri, hostTicket, room.InviteExpiry, resumeTicket);
        }
    }

    internal ClaimReceipt? Claim(Guid id, string? joinCode,string? node)
    {
        lock (gate)
        {
            var room = rooms.GetValueOrDefault(id);
            if (room?.JoinHash is not { } expected || room.InviteExpiry <= clock.GetUtcNow() || !Matches(expected, joinCode)) return null;
            string ticket = NewToken(), resumeTicket = NewToken();
            room.JoinHash = null;
            room.GuestHash = Digest(ticket);
            room.GuestResume = Digest(resumeTicket);
            room.GuestNode=node;
            return new(ticket, room.InviteExpiry, resumeTicket);
        }
    }

    internal bool Approve(Guid id, string? ticket, PairRoom proof)
    {
        lock (gate)
        {
            if (pairs.TryGetValue(id,out var existing))
                return existing.ClosedAt==null && existing.NodeId==proof.NodeId && Matches(existing.Host,ticket) && rooms.TryGetValue(id,out var approved) &&
                    TicketMatches(approved,false,proof.GuestTicket);
            if (pairs.Count(pair=>pair.Value.ClosedAt==null)>=4096 || !rooms.TryGetValue(id,out var room) || room.Blocked || room.HostResume==null ||
                room.GuestResume==null || room.GuestNode==null || room.GuestNode!=proof.NodeId ||
                !Matches(room.HostResume,ticket) || !TicketMatches(room,false,proof.GuestTicket)) return false;
            pairs.Add(id,new(room.HostResume,room.GuestResume,proof.NodeId));
            try { SavePairs(); }
            catch { pairs.Remove(id); throw; }
            return true;
        }
    }

    internal (ClaimReceipt? Receipt,bool? ClosedByHost) Renew(Guid id,string? resumeTicket)
    {
        lock(gate)
        {
            if(!pairs.TryGetValue(id,out var pair)) return (null,null);
            bool host=Matches(pair.Host,resumeTicket);
            if(!host && !Matches(pair.Guest,resumeTicket)) return (null,null);
            if(pair.ClosedAt!=null)return (null,pair.ClosedByHost);
            if(!rooms.TryGetValue(id,out var room))
            {
                var now=clock.GetUtcNow();
                foreach(var slot in rooms.Where(slot=>slot.Value.LatestExpiry<=now && !slot.Value.Relay.Active).ToArray())
                    rooms.Remove(slot.Key);
                if(rooms.Count>=Capacity) return (null,null);
                rooms[id]=room=new Room(now,null,null);
            }
            string ticket=NewToken();
            var expiry=clock.GetUtcNow()+Lifetime;
            if(host) { room.HostHash=Digest(ticket); room.HostExpiry=expiry; }
            else { room.GuestHash=Digest(ticket); room.GuestExpiry=expiry; }
            return (new(ticket,expiry),null);
        }
    }

    internal bool Unpair(Guid id,string? resumeTicket)
    {
        lock(gate)
        {
            if(!pairs.TryGetValue(id,out var pair)) return true;
            bool host=Matches(pair.Host,resumeTicket);
            if(!host && !Matches(pair.Guest,resumeTicket)) return false;
            if(pair.ClosedAt!=null)return true;
            pairs[id]=pair with {ClosedAt=clock.GetUtcNow(),ClosedByHost=host};
            try { SavePairs(); }
            catch { pairs[id]=pair; throw; }
            if(rooms.TryGetValue(id,out var room))
            {
                room.Blocked=true;
                room.HostHash=room.GuestHash=null;
                room.Member(true)?.Frames.Writer.TryComplete();
                room.Member(false)?.Frames.Writer.TryComplete();
                room.Relay.Revoke();
            }
            return true;
        }
    }

    private static Dictionary<Guid,RoomPair> LoadPairs(string path) => File.Exists(path)
        ? JsonSerializer.Deserialize<Dictionary<Guid,RoomPair>>(File.ReadAllBytes(path)) ?? throw new InvalidDataException("信令配对记录无效")
        : [];

    private void SavePairs()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(pairingsPath)!);
        string temp=pairingsPath+".new";
        var keepAfter=clock.GetUtcNow().AddDays(-30);
        var retained=pairs.Where(entry=>entry.Value.ClosedAt==null || entry.Value.ClosedAt>=keepAfter)
            .ToDictionary(entry=>entry.Key,entry=>entry.Value);
        File.WriteAllBytes(temp,JsonSerializer.SerializeToUtf8Bytes(retained));
        if(!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp,UnixFileMode.UserRead|UnixFileMode.UserWrite);
        File.Move(temp,pairingsPath,true);
        foreach(var id in pairs.Keys.Where(id=>!retained.ContainsKey(id)).ToArray())pairs.Remove(id);
    }

    private bool TicketMatches(Room room,bool host,string? ticket) => !room.Blocked &&
        (host?room.HostExpiry:room.GuestExpiry)>clock.GetUtcNow() && Matches(host?room.HostHash:room.GuestHash,ticket);

    internal (Peer? Peer, bool Busy) Connect(Guid id, string? ticket)
    {
        lock (gate)
        {
            var room = rooms.GetValueOrDefault(id);
            if (room is null) return (null, false);
            bool host = TicketMatches(room,true,ticket);
            if (!host && !TicketMatches(room,false,ticket)) return (null, false);
            if (room.Member(host) is not null) return (null, true);
            var peer = new Peer(room, host,host?room.HostExpiry:room.GuestExpiry);
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

    internal TimeSpan TimeRemaining(Peer peer) => peer.ExpiresAt - clock.GetUtcNow();

    internal (TurnRoom.Lease? Lease, int Status) OpenRelay(Guid id, string? ticket)
    {
        lock (gate)
        {
            var room = rooms.GetValueOrDefault(id);
            if (room == null || room.GuestHash==null && !pairs.ContainsKey(id)) return (null, 401);
            bool host = TicketMatches(room,true,ticket);
            if (!host && !TicketMatches(room,false,ticket)) return (null, 401);
            if (turnExecutable == null) return (null, 503);
            var lease = room.Relay.Acquire(host, turnExecutable);
            return (lease, lease == null ? 409 : 200);
        }
    }

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

    internal sealed class Room(DateTimeOffset expiresAt, byte[]? hostHash, byte[]? joinHash)
    {
        internal DateTimeOffset InviteExpiry { get; } = expiresAt;
        internal DateTimeOffset HostExpiry { get; set; } = expiresAt;
        internal DateTimeOffset GuestExpiry { get; set; } = expiresAt;
        internal DateTimeOffset LatestExpiry => HostExpiry>GuestExpiry?HostExpiry:GuestExpiry;
        internal byte[]? HostHash { get; set; } = hostHash;
        internal byte[]? JoinHash { get; set; } = joinHash;
        internal byte[]? GuestHash { get; set; }
        internal byte[]? HostResume { get; init; }
        internal byte[]? GuestResume { get; set; }
        internal string? GuestNode { get; set; }
        internal bool Blocked { get; set; }
        internal TurnRoom Relay { get; } = new();
        private Peer? host, guest;

        internal Peer? Member(bool asHost) => asHost ? host : guest;
        internal void SetMember(bool asHost, Peer? peer)
        {
            if (asHost) host = peer;
            else guest = peer;
        }
    }

    internal sealed class Peer(Room room, bool host,DateTimeOffset expiresAt)
    {
        internal Room Room { get; } = room;
        internal bool Host { get; } = host;
        internal DateTimeOffset ExpiresAt { get; } = expiresAt;
        internal Channel<byte[]> Frames { get; } = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(16)
        {
            SingleReader = true,
            SingleWriter = true
        });
    }
}

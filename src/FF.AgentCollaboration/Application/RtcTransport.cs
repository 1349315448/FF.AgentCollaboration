using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Storage;
using FF.AgentCollaboration.Transport;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace FF.AgentCollaboration.Application;

/// <summary>通过已验证的信令服务器协商后台传输。联邦协议继续在对端 TLS 连接中鉴权。</summary>
internal sealed class RtcTransport(WorkbenchStartup startup, FederationNetwork network, IConfiguration configuration, SignalingSettings settings,
    IHostApplicationLifetime lifetime,WorkbenchEvents events) : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, Session> sessions = new();
    private sealed record Binding(string Id,bool Group,bool Confirmed=false);
    private sealed record Credential(string Room, string Ticket, DateTimeOffset ExpiresAt, bool Host, string Window, string? ServerIdentity,
        string? ResumeTicket=null,Binding[]? Bindings=null);
    private sealed record Invitation(string RoomId, string Link, string HostTicket, DateTimeOffset ExpiresAt,string? ResumeTicket);
    private sealed record Receipt(string Ticket, DateTimeOffset ExpiresAt,string? ResumeTicket=null);
    internal sealed record PairProof(string GuestTicket,string? Group);
    private sealed class Session(Credential credential, SignalingTrust trust)
    {
        internal Credential Credential { get; } = credential;
        internal SignalingTrust Trust { get; } = trust;
        internal volatile RtcWorker? Worker;
        internal volatile Receipt Lease = new(credential.Ticket,credential.ExpiresAt);
        internal readonly CancellationTokenSource Stop = new();
    }

    internal static string? Executable(IConfiguration settings)
    {
        string? configured = settings["Signaling:BridgeExecutable"];
        string name = OperatingSystem.IsWindows() ? "FF.AgentCollaboration.WebRtc.exe" : "FF.AgentCollaboration.WebRtc";
        string path = Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? name : configured, AppContext.BaseDirectory);
        return File.Exists(path) ? path : null;
    }

    internal bool HasBridge => network.RtcPort > 0;
    internal bool Enabled => HasBridge && settings.Read().Values.IsConfigured;

    internal string? BridgeBlockReason() => Executable(configuration) == null ? "bridge_missing" : HasBridge ? null : "restart_required";

    internal string? ShareBlockReason(SignalingSettings.State state)
    {
        if (state.ReadError) return "settings_unreadable";
        if (!state.Values.IsConfigured) return "signaling_not_configured";
        return BridgeBlockReason() ?? (string.IsNullOrWhiteSpace(state.Values.SigningKey) ? "signing_key_missing" : null);
    }

    internal void Restore()
    {
        if (!HasBridge) return;
        SignalingTrust? trust;
        try { trust = settings.Read().Values.Trust(); }
        catch (WorkbenchError) { return; }
        if (trust == null) return;
        using var db = new WorkbenchStore(startup.Root);
        foreach (string record in db.RtcRooms())
        {
            var room = JsonSerializer.Deserialize<Credential>(record, Web);
            if (room != null && room.ServerIdentity == trust.Identity &&
                (room.Bindings is {Length:>0} || room.ExpiresAt>DateTimeOffset.UtcNow &&
                (!room.Host || db.Share(room.Window)!=null || db.HasHostedGroup(room.Window)))) Activate(room, trust);
        }
    }

    internal async Task<string> Invite(string windowId, CancellationToken ct)
    {
        var values = settings.Read().Values;
        var trust = Require(values);
        string? key = values.SigningKey;
        if (string.IsNullOrWhiteSpace(key)) throw new WorkbenchError("请在提供方工作台配置签发密钥");
        using var db = new WorkbenchStore(startup.Root);
        var window = db.Window(windowId);
        var share = db.Share(window.Id) ?? throw new WorkbenchError("请先开启 Agent 外部协作");
        var node = db.FederationNode();
        string pin = network.Certificate()!.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256);
        using var client = trust.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "invitations");
        request.Headers.Add("X-Lx-Service-Key", key);
        request.Content = JsonContent.Create(new { node.NodeId, windowId = window.Id, pin });
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new WorkbenchError("信令服务器未签发邀请，请检查签发密钥和房间容量");
        var invite = await response.Content.ReadFromJsonAsync<Invitation>(ct) ?? throw new IOException("信令邀请响应为空");
        string roomId = WorkbenchInput.Id(invite.RoomId);
        var link = new Uri(invite.Link);
        if (!FederationPolicy.IsSecret(invite.HostTicket) || !FederationPolicy.IsSecret(link.Fragment.TrimStart('#')))
            throw new IOException("信令邀请响应无效");
        var room = new Credential(roomId, invite.HostTicket, invite.ExpiresAt, true, window.Id, trust.Identity,invite.ResumeTicket);
        // 工作台授权口令只从提供方传给对端，不交给信令服务器。
        var shared = new UriBuilder("ffcp", trust.Origin.IdnHost, trust.Origin.IsDefaultPort ? -1 : trust.Origin.Port, "/signal/" + roomId)
        {
            Query = $"node={node.NodeId}&window={window.Id}&topic={window.TopicId}&pin={pin}&agent={Uri.EscapeDataString(window.Name)}",
            Fragment = link.Fragment.TrimStart('#') + "." + share.Token
        };
        db.SetFedSetting("rtc_room." + roomId, JsonSerializer.Serialize(room, Web));
        Activate(room, trust);
        return shared.Uri.AbsoluteUri;
    }

    internal async Task<string> Join(RemoteApplication input, Uri invite, CancellationToken ct,GroupSelection? group=null)
    {
        var trust = Require(settings.Read().Values);
        if (invite.Scheme != "ffcp" || invite.UserInfo.Length != 0 ||
            !string.Equals(invite.IdnHost, trust.Origin.IdnHost, StringComparison.OrdinalIgnoreCase) ||
            (invite.Port == -1 ? 443 : invite.Port) != trust.Origin.Port)
            throw new WorkbenchError("邀请链接的信令服务器与本机已信任的地址不一致");
        string roomId = WorkbenchInput.Id(invite.AbsolutePath["/signal/".Length..]);
        string[] secrets = invite.Fragment.TrimStart('#').Split('.');
        var fields = QueryHelpers.ParseQuery(invite.Query);
        string pin = fields["pin"].ToString();
        if (secrets.Length != 2 || secrets.Any(value => !FederationPolicy.IsSecret(value)) || !FederationPolicy.IsSecret(pin))
            throw new WorkbenchError("邀请加入码或证书指纹无效");
        string node = WorkbenchInput.Id(fields["node"]), window = WorkbenchInput.Id(fields["window"]);
        string topic = WorkbenchInput.Id(input.TopicId);
        string agent = WorkbenchInput.AgentName(fields["agent"]);
        string purpose = WorkbenchInput.Text(input.Purpose, "申请用途", 300);
        using var db = new WorkbenchStore(startup.Root);
        if (db.FederationNode().NodeId == node) throw new WorkbenchError("不能添加本工作台的 Agent", WorkbenchFailure.Conflict);
        if (db.Topic(topic).Status != "active") throw new WorkbenchError("请先重新打开话题");
        if (input.ResumeRemoteId != null) db.RequireRemoteResume(input.ResumeRemoteId, topic, node, window, pin);
        var room = db.FedSetting("rtc_room." + roomId) is { } saved ? JsonSerializer.Deserialize<Credential>(saved, Web) : null;
        if (room == null)
        {
            using var client = trust.CreateClient();
            using var claim = new HttpRequestMessage(HttpMethod.Post, "rooms/" + roomId + "/claim");
            claim.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secrets[0]);
            claim.Content=JsonContent.Create(new {db.FederationNode().NodeId});
            using var response = await client.SendAsync(claim, ct);
            if (!response.IsSuccessStatusCode) throw new WorkbenchError("邀请已过期或已被使用，请重新获取跨内网链接", WorkbenchFailure.Conflict);
            var receipt = await response.Content.ReadFromJsonAsync<Receipt>(ct) ?? throw new IOException("信令服务器未返回票据");
            if (!FederationPolicy.IsSecret(receipt.Ticket)) throw new IOException("信令票据无效");
            room = new(roomId, receipt.Ticket, receipt.ExpiresAt, false, window, trust.Identity,receipt.ResumeTicket);
            db.SetFedSetting("rtc_room." + roomId, JsonSerializer.Serialize(room, Web));
        }
        if (room.Host || room.Window != window || room.ExpiresAt <= DateTimeOffset.UtcNow || room.ServerIdentity != trust.Identity)
            throw new WorkbenchError("跨内网邀请已失效，请重新获取");
        string id = group==null ? db.AddRemote(topic, "rtc://" + roomId, pin, node, window, agent, secrets[1], purpose,
            input.ContactSelected ? new ContactBinding(topic, input.AgentId) : null, input.ResumeRemoteId)
            : db.StartGroupCollaboration(topic,"rtc://"+roomId,pin,node,window,secrets[1],purpose,group,
                fields.TryGetValue("topic",out var remoteTopic)?WorkbenchInput.Id(remoteTopic):null);
        Activate(room, trust);
        return id;
    }

    internal int PeerPort(string endpoint)
    {
        string room = endpoint["rtc://".Length..];
        var worker = sessions.GetValueOrDefault(room)?.Worker;
        if (worker != null && worker.Port > 0 && worker.Online) return worker.Port;
        throw new HttpRequestException("跨内网协作通道尚未连通");
    }

    internal async Task<bool> ApprovePair(string roomId,PairProof proof,string token,string node,CancellationToken ct)
    {
        using var db = new WorkbenchStore(startup.Root);
        var room=LoadRoom(db,roomId);
        if(room is not {Host:true,ResumeTicket:{}} || !sessions.TryGetValue(roomId,out var session) ||
            !FederationPolicy.IsSecret(proof.GuestTicket)) return false;
        string grant=db.AuthorizeRtcBinding(token,node,room.Window,proof.Group);
        var binding=new Binding(grant,proof.Group!=null);
        if(Confirmed(room,binding)) return true;
        RememberBinding(roomId,binding);
        using var response=await CallSignal(session.Trust,room,"pair",HttpMethod.Post,new {proof.GuestTicket,NodeId=node},ct);
        if(!response.IsSuccessStatusCode) return false;
        RememberBinding(roomId,binding with {Confirmed=true});
        return true;
    }

    internal async Task ConfirmPair(RemoteConnection remote,bool group,Func<string,PairProof,HttpRequestMessage> request,HttpClient client,CancellationToken ct)
    {
        if(!remote.Endpoint.StartsWith("rtc://",StringComparison.Ordinal))return;
        string roomId=remote.Endpoint[6..];
        if(!sessions.TryGetValue(roomId,out var session) || session.Credential.ResumeTicket==null) return;
        var binding=new Binding(remote.Id,group);
        using var db=new WorkbenchStore(startup.Root);
        if(Confirmed(LoadRoom(db,roomId),binding)) return;
        RememberBinding(roomId,binding);
        try
        {
            using var confirmation=request(roomId,new(session.Lease.Ticket,group?remote.Id:null));
            using var response=await client.SendAsync(confirmation,ct);
            if(response.StatusCode==System.Net.HttpStatusCode.NoContent) RememberBinding(roomId,binding with {Confirmed=true});
        }
        catch (Exception error) when (!ct.IsCancellationRequested && error is HttpRequestException or IOException or OperationCanceledException)
        { /* 配对失败只影响下次握手，当前协作仍可继续。 */ }
    }

    private static Credential? LoadRoom(WorkbenchStore db,string id) => db.FedSetting("rtc_room."+id) is string raw
        ? JsonSerializer.Deserialize<Credential>(raw,Web):null;

    private static bool Confirmed(Credential? room,Binding binding) =>
        room?.Bindings?.Any(item=>item.Id==binding.Id && item.Group==binding.Group && item.Confirmed)==true;

    private readonly Lock bindingGate=new();
    private void RememberBinding(string id,Binding binding)
    {
        lock(bindingGate)
        {
            using var db=new WorkbenchStore(startup.Root);
            if(LoadRoom(db,id) is not { } current || Confirmed(current,binding)||current.Bindings?.Contains(binding)==true) return;
            var others=(current.Bindings??[]).Where(item=>item.Id!=binding.Id || item.Group!=binding.Group);
            db.SetFedSetting("rtc_room."+id,JsonSerializer.Serialize(current with {Bindings=[..others,binding]},Web));
        }
    }

    private static async Task<HttpResponseMessage> CallSignal(SignalingTrust trust,Credential room,string action,HttpMethod method,object? body,CancellationToken ct)
    {
        using var client=trust.CreateClient();
        using var request=new HttpRequestMessage(method,"rooms/"+room.Room+"/"+action);
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",room.ResumeTicket);
        if(body!=null)request.Content=JsonContent.Create(body);
        return await client.SendAsync(request,ct);
    }

    internal async Task CheckPairings(CancellationToken ct)
    {
        if(!Enabled) return;
        var trust=settings.Read().Values.Trust();
        if(trust==null)return;
        using var db=new WorkbenchStore(startup.Root);
        foreach(var raw in db.RtcRooms())
        {
            var room=JsonSerializer.Deserialize<Credential>(raw,Web);
            if(room?.Bindings is not {Length:>0} || room.ServerIdentity!=trust.Identity ||
                room.Bindings.Any(binding=>db.RtcBindingActive(room.Room,room.Host,binding.Group,binding.Id)))continue;
            try
            {
                using var result=await CallSignal(trust,room,"pair",HttpMethod.Delete,null,ct);
                if(!result.IsSuccessStatusCode)continue;
                db.DeleteRtcRoom(room.Room);
                if(sessions.TryGetValue(room.Room,out var session)) await session.Stop.CancelAsync();
            }
            catch (Exception error) when (!ct.IsCancellationRequested && error is HttpRequestException or IOException or OperationCanceledException)
            { /* 保留待撤销引用，下次检查再通知信令。 */ }
        }
    }

    private SignalingTrust Require(SignalingSettings.Configuration values) => HasBridge && values.Trust() is { } trust
        ? trust : throw new WorkbenchError("请检查信令配置、公钥指纹及后台 WebRTC 组件");

    private void Activate(Credential credential, SignalingTrust trust)
    {
        var session = new Session(credential, trust);
        if (sessions.TryAdd(credential.Room, session)) _ = Run(session);
    }

    private async Task Run(Session session)
    {
        using var run=CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping,session.Stop.Token);
        var ct = run.Token;
        var room = session.Credential;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var state=new WorkbenchStore(startup.Root);
                    var current=LoadRoom(state,room.Room);
                    if(current==null) return;
                    if(session.Lease.ExpiresAt<=DateTimeOffset.UtcNow && session.Worker?.Online==true)
                        await session.Worker.WaitForExit(ct);
                    if(current.Bindings is {Length:>0})
                    {
                        if(!current.Bindings.Any(binding=>state.RtcBindingActive(room.Room,room.Host,binding.Group,binding.Id))) return;
                        using var renewed=await CallSignal(session.Trust,room,"renew",HttpMethod.Post,null,ct);
                        if(renewed.StatusCode==System.Net.HttpStatusCode.Gone)
                        {
                            if(!room.Host && renewed.Headers.TryGetValues("X-FF-Rtc-Closed-By",out var closedBy) && closedBy.Contains("host"))
                                foreach(var binding in current.Bindings)state.RevokeRtcRemote(room.Room,binding.Group,binding.Id);
                            state.DeleteRtcRoom(room.Room);
                            events.RefreshPages();
                            return;
                        }
                        if(renewed.IsSuccessStatusCode)
                        {
                            session.Lease=await renewed.Content.ReadFromJsonAsync<Receipt>(ct) ?? throw new IOException("续票响应无效");
                            if(!FederationPolicy.IsSecret(session.Lease.Ticket)) throw new IOException("续票凭据无效");
                            foreach(var binding in current.Bindings.Where(binding=>!binding.Confirmed))
                                RememberBinding(room.Room,binding with {Confirmed=true});
                        }
                        else if(renewed.StatusCode==System.Net.HttpStatusCode.Unauthorized && !current.Bindings.Any(binding=>binding.Confirmed))
                        {
                            if(session.Lease.ExpiresAt<=DateTimeOffset.UtcNow)return;
                        }
                        else throw new HttpRequestException("信令暂未签发新票据");
                    }
                    else if(session.Lease.ExpiresAt<=DateTimeOffset.UtcNow) return;
                    if (room.Host)
                    {
                        if (state.Share(room.Window) == null && !state.HasHostedGroup(room.Window)) return;
                    }
                    // 现有房间持续信任签发时的服务器，保存新设置不会把旧票据发给新服务器。
                    using var websocket = await session.Trust.Open(room.Room, session.Lease.Ticket, ct);
                    RtcWorker? peer = null;
                    while (!ct.IsCancellationRequested)
                    {
                        var ready = peer == null ? await Receive(websocket, "ready", ct) : await WaitForPeer(websocket, peer, ct);
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeout.CancelAfter(TimeSpan.FromSeconds(45));
                        var ice = settings.Read().Values.Servers.Select(server => new
                        {
                            urls = new[] { server.Url! },
                            // Pion 的 ICE 配置将认证字段按字符串读取，STUN 使用空字符串。
                            username = server.Username ?? "",
                            credential = server.Credential ?? ""
                        }).ToList();
                        var origin = session.Trust.Origin;
                        if (ready.TryGetProperty("stun", out var stun) && stun.ValueKind == JsonValueKind.True)
                        {
                            string url = $"stun:{origin.Host}:{origin.Port}";
                            if (!ice.Any(server => server.urls.Contains(url)))
                                ice.Add(new { urls = new[] { url }, username = "", credential = "" });
                        }
                        TurnTunnel? tunnel = null;
                        if (ready.TryGetProperty("turnPassword", out var password) && FederationPolicy.IsSecret(password.GetString()))
                        {
                            tunnel = new TurnTunnel(session.Trust, room.Room, session.Lease.Ticket);
                            ice.Add(new { urls = new[] { $"turn:127.0.0.1:{tunnel.Port}?transport=tcp" },
                                username = "member", credential = password.GetString()! });
                        }
                        try { peer = new RtcWorker(Executable(configuration)!, tunnel); }
                        catch { tunnel?.Dispose(); throw; }
                        session.Worker?.Dispose();
                        session.Worker = peer;
                        if (room.Host)
                        {
                            await peer.Write(new { role = "provider", federationPort = network.RtcPort, iceServers = ice }, timeout.Token);
                            await Send(websocket, "offer", (await peer.Read(timeout.Token)).GetProperty("sdp").GetString(), timeout.Token);
                            var answer = await Receive(websocket, "answer", timeout.Token);
                            await peer.Write(new { answer = answer.GetProperty("sdp").GetString() }, timeout.Token);
                        }
                        else
                        {
                            string offer = (await Receive(websocket, "offer", timeout.Token)).GetProperty("sdp").GetString()!;
                            await peer.Write(new { role = "requester", offer, iceServers = ice }, timeout.Token);
                            await Send(websocket, "answer", (await peer.Read(timeout.Token)).GetProperty("sdp").GetString(), timeout.Token);
                        }
                        await peer.Ready(timeout.Token);
                    }
                }
                catch (Exception error) when (!ct.IsCancellationRequested && error is IOException or WebSocketException or HttpRequestException
                    or WorkbenchError or OperationCanceledException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
                { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            sessions.TryRemove(room.Room, out _);
            session.Worker?.Dispose();
        }
    }

    private static async Task<JsonElement> WaitForPeer(ClientWebSocket websocket, RtcWorker peer, CancellationToken ct)
    {
        // 信令套接字可能仍然正常，但承载状态同步的数据通道已随工作进程退出。
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct);
        async Task WatchExit()
        {
            try
            {
                await peer.WaitForExit(waiting.Token);
                waiting.Cancel();
            }
            catch (OperationCanceledException) when (waiting.IsCancellationRequested) { }
        }
        var watch = WatchExit();
        try { return await Receive(websocket, "ready", waiting.Token); }
        finally
        {
            await waiting.CancelAsync();
            await watch;
        }
    }

    private static async Task<JsonElement> Receive(ClientWebSocket websocket, string expected, CancellationToken ct)
    {
        var buffer = new byte[32 * 1024];
        while (true)
        {
            int count = 0;
            ValueWebSocketReceiveResult part;
            do
            {
                part = await websocket.ReceiveAsync(buffer.AsMemory(count), ct);
                count += part.Count;
                if (part.MessageType != WebSocketMessageType.Text || (count == buffer.Length && !part.EndOfMessage))
                    throw new IOException("信令连接已关闭或帧无效");
            } while (!part.EndOfMessage);
            using var message = JsonDocument.Parse(buffer.AsMemory(0, count));
            if (message.RootElement.GetProperty("type").GetString() == expected)
                return message.RootElement.Clone();
        }
    }

    private static ValueTask Send(ClientWebSocket websocket, string type, string? sdp, CancellationToken ct) =>
        websocket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { type, sdp }).AsMemory(), WebSocketMessageType.Text, true, ct);

    public void Dispose()
    {
        foreach (var session in sessions.Values) session.Worker?.Dispose();
    }
}

using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Storage;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace FF.AgentCollaboration.Application;

/// <summary>通过已验证的信令服务器协商后台传输。联邦协议继续在对端 TLS 连接中鉴权。</summary>
internal sealed class RtcTransport(WorkbenchStartup startup, FederationNetwork network, IConfiguration configuration, SignalingSettings settings,
    IHostApplicationLifetime lifetime) : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, Session> sessions = new();
    private sealed record Credential(string Room, string Ticket, DateTimeOffset ExpiresAt, bool Host, string Window, string? ServerIdentity);
    private sealed record Invitation(string RoomId, string Link, string HostTicket, DateTimeOffset ExpiresAt);
    private sealed record Receipt(string Ticket, DateTimeOffset ExpiresAt);
    private sealed class Session(Credential credential, SignalingTrust trust)
    {
        internal Credential Credential { get; } = credential;
        internal SignalingTrust Trust { get; } = trust;
        internal volatile RtcWorker? Worker;
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
            if (room != null && room.ExpiresAt > DateTimeOffset.UtcNow && room.ServerIdentity == trust.Identity &&
                (!room.Host || db.Share(room.Window) != null)) Activate(room, trust);
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
        var room = new Credential(roomId, invite.HostTicket, invite.ExpiresAt, true, window.Id, trust.Identity);
        // 工作台授权口令只从提供方传给对端，不交给信令服务器。
        var shared = new UriBuilder("ffcp", trust.Origin.IdnHost, trust.Origin.IsDefaultPort ? -1 : trust.Origin.Port, "/signal/" + roomId)
        {
            Query = $"node={node.NodeId}&window={window.Id}&pin={pin}&agent={Uri.EscapeDataString(window.Name)}",
            Fragment = link.Fragment.TrimStart('#') + "." + share.Token
        };
        db.SetFedSetting("rtc_room." + roomId, JsonSerializer.Serialize(room, Web));
        Activate(room, trust);
        return shared.Uri.AbsoluteUri;
    }

    internal async Task<string> Join(RemoteApplication input, Uri invite, CancellationToken ct)
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
        var room = db.FedSetting("rtc_room." + roomId) is { } saved ? JsonSerializer.Deserialize<Credential>(saved, Web) : null;
        if (room == null)
        {
            using var client = trust.CreateClient();
            using var claim = new HttpRequestMessage(HttpMethod.Post, "rooms/" + roomId + "/claim");
            claim.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secrets[0]);
            using var response = await client.SendAsync(claim, ct);
            if (!response.IsSuccessStatusCode) throw new WorkbenchError("邀请已过期或已被使用，请重新获取跨内网链接", WorkbenchFailure.Conflict);
            var receipt = await response.Content.ReadFromJsonAsync<Receipt>(ct) ?? throw new IOException("信令服务器未返回票据");
            if (!FederationPolicy.IsSecret(receipt.Ticket)) throw new IOException("信令票据无效");
            room = new(roomId, receipt.Ticket, receipt.ExpiresAt, false, window, trust.Identity);
            db.SetFedSetting("rtc_room." + roomId, JsonSerializer.Serialize(room, Web));
        }
        if (room.Host || room.Window != window || room.ExpiresAt <= DateTimeOffset.UtcNow || room.ServerIdentity != trust.Identity)
            throw new WorkbenchError("跨内网邀请已失效，请重新获取");
        string id = db.AddRemote(topic, "rtc://" + roomId, pin, node, window, agent, secrets[1], purpose,
            input.ContactSelected ? new ContactBinding(topic, input.AgentId) : null);
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

    private SignalingTrust Require(SignalingSettings.Configuration values) => HasBridge && values.Trust() is { } trust
        ? trust : throw new WorkbenchError("请检查信令配置、公钥指纹及后台 WebRTC 组件");

    private void Activate(Credential credential, SignalingTrust trust)
    {
        var session = new Session(credential, trust);
        if (sessions.TryAdd(credential.Room, session)) _ = Run(session);
    }

    private async Task Run(Session session)
    {
        var ct = lifetime.ApplicationStopping;
        var room = session.Credential;
        try
        {
            while (DateTimeOffset.UtcNow < room.ExpiresAt && !ct.IsCancellationRequested)
            {
                try
                {
                    if (room.Host)
                    {
                        using var db = new WorkbenchStore(startup.Root);
                        if (db.Share(room.Window) == null) return;
                    }
                    // 现有房间持续信任签发时的服务器，保存新设置不会把旧票据发给新服务器。
                    using var websocket = await session.Trust.Open(room.Room, room.Ticket, ct);
                    while (!ct.IsCancellationRequested)
                    {
                        await Receive(websocket, "ready", ct);
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeout.CancelAfter(TimeSpan.FromSeconds(45));
                        var ice = configuration.GetSection("Signaling:IceServers").Get<RtcIceServer[]>() ?? [];
                        var peer = new RtcWorker(Executable(configuration)!);
                        session.Worker?.Dispose();
                        session.Worker = peer;
                        if (room.Host)
                        {
                            await peer.Write(new { role = "provider", federationPort = network.RtcPort, iceServers = ice }, timeout.Token);
                            await Send(websocket, "offer", (await peer.Read(timeout.Token)).GetProperty("sdp").GetString(), timeout.Token);
                            var answer = await Receive(websocket, "answer", timeout.Token);
                            await peer.Write(new { answer }, timeout.Token);
                        }
                        else
                        {
                            string offer = await Receive(websocket, "offer", timeout.Token);
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
            // 已协商的数据通道可以在配对票据到期后继续承载联邦 TLS。
            while (!ct.IsCancellationRequested && session.Worker?.Online == true)
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            sessions.TryRemove(room.Room, out _);
            session.Worker?.Dispose();
        }
    }

    private static async Task<string> Receive(ClientWebSocket websocket, string expected, CancellationToken ct)
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
                return message.RootElement.TryGetProperty("sdp", out var sdp) ? sdp.GetString()! : "";
        }
    }

    private static ValueTask Send(ClientWebSocket websocket, string type, string? sdp, CancellationToken ct) =>
        websocket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { type, sdp }).AsMemory(), WebSocketMessageType.Text, true, ct);

    public void Dispose()
    {
        foreach (var session in sessions.Values) session.Worker?.Dispose();
    }
}

internal sealed record RtcIceServer(string[] Urls, string? Username, string? Credential);

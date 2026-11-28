using System.Net;
using System.Text.Json;
using System.Threading.RateLimiting;
using FF.AgentCollaboration.Application;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

namespace FF.AgentCollaboration.Presentation.Http;

internal sealed class FederationEndpoints(WorkbenchStartup startup, FederationNetwork network, WorkbenchEvents events,
    RtcTransport rtc, SignalingSettings signaling) : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly PartitionedRateLimiter<string> limiter = PartitionedRateLimiter.Create<string, string>(ip =>
        RateLimitPartition.GetTokenBucketLimiter(ip, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = 100,
            TokensPerPeriod = 20,
            ReplenishmentPeriod = TimeSpan.FromSeconds(10),
            AutoReplenishment = true,
            QueueLimit = 0
        }));
    private readonly SemaphoreSlim streams = new(32);
    private WorkbenchStore Store() => new(startup.Root);
    private static string Token(HttpContext c) => ((string)c.Items["peerToken"]!);
    private static string Node(HttpContext c) => ((string)c.Items["peerNode"]!);
    private static string Name(HttpContext c) => ((string)c.Items["peerName"]!);

    private object SignalingView(SignalingSettings.State state)
    {
        string? bridgeBlockReason = rtc.BridgeBlockReason();
        return new
        {
            origin = state.Values.Origin,
            fingerprint = state.Values.Fingerprint,
            hasSigningKey = !string.IsNullOrWhiteSpace(state.Values.SigningKey),
            iceServers = state.Values.Servers.Select(server => new { server.Url, server.Username, hasCredential = server.Credential != null }),
            bridgeAvailable = bridgeBlockReason == null,
            bridgeBlockReason,
            readError = state.ReadError
        };
    }

    private bool RtcListener(HttpContext c) => network.RtcPort > 0 && c.Connection.LocalPort == network.RtcPort &&
        Equals(c.Connection.LocalIpAddress, IPAddress.Loopback) && Equals(c.Connection.RemoteIpAddress, IPAddress.Loopback) &&
        c.Request.Host.Host == "127.0.0.1";

    // 使用已匹配端点的标记划分网络面，未知路由、静态资源、终端升级请求都走本机边界。
    internal bool Admit(HttpContext c)
    {
        if (c.GetEndpoint()?.Metadata.GetMetadata<FederationEndpoints>() == null) return false;
        if (!c.Request.IsHttps || c.Connection.RemoteIpAddress is not { } ip ||
            (!RtcListener(c) && (!Equals(c.Connection.LocalIpAddress, network.ActiveIp) || !network.AdmitsPeer(ip, c.Request.Host))) ||
            c.Request.Headers.ContainsKey("Origin")) throw new FederationException(403, "lan_only");
        using var lease = limiter.AttemptAcquire(ip.ToString());
        if (!lease.IsAcquired) throw new FederationException(429, "rate_limited");
        string auth = c.Request.Headers.Authorization.ToString();
        if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || !FederationPolicy.IsSecret(auth[7..]))
            throw new FederationException(401, "bearer_required");
        c.Items["peerToken"] = auth[7..];
        c.Items["peerNode"] = WorkbenchInput.Id(c.Request.Headers["X-FF-Node-Id"]);
        c.Items["peerName"] = FederationPolicy.Name(Uri.UnescapeDataString(c.Request.Headers["X-FF-Node-Name"].ToString()));
        int bodyLimit = c.GetEndpoint()?.Metadata.GetMetadata<UploadBody>() is { } upload
            ? (upload.Attachments ? WorkbenchStore.PeerBinaryLimit : WorkbenchStore.PeerFileLimit)
            : c.GetEndpoint()?.Metadata.GetMetadata<GroupExchangeBody>()!=null ? 131072 : 65536;
        if (c.Request.ContentLength > bodyLimit) throw new FederationException(413, "body_too_large");
        var size = c.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (size is { IsReadOnly: false }) size.MaxRequestBodySize = bodyLimit;
        if (!startup.Ready.IsCompletedSuccessfully) throw new FederationException(503, "node_not_ready");
        using var store = Store();
        if (store.FederationNode().NodeName == null) throw new FederationException(503, "node_not_ready");
        return true;
    }
    internal void Map(WebApplication app)
    {
        var local = app.MapGroup("/api/peers");
        local.MapGet("/mail/{seq:long}", (long seq) => { using var db=Store(); return new { text=db.MailBody(seq) }; });
        local.MapGet("/mail/{seq:long}/document", (long seq) => { using var db = Store(); return Results.Bytes(db.MailDocument(seq), "text/plain; charset=utf-8"); });
        local.MapGet("/mail/{seq:long}/attachments", (long seq) => { using var db = Store(); return db.MailAttachments(seq); });
        local.MapGet("/mail/{seq:long}/attachments/{id}", (long seq, string id) =>
        { using var db = Store(); var file = db.MailAttachment(seq, id); return Results.File(file.Bytes, "application/octet-stream", file.Name); });
        local.MapGet("/attachment-approvals", () => { using var db = Store(); return db.AttachmentApprovals(); });
        local.MapPost("/attachment-approvals/{id}", (string id, AttachmentDecision body) =>
        { using var db = Store(); db.DecideAttachment(id, body.Action); return Results.NoContent(); });
        local.MapGet("/windows/{id}/attachment-permission", (string id) =>
        { using var db = Store(); return new { allowed = db.AttachmentPermission(WorkbenchInput.Id(id)) }; });
        local.MapDelete("/windows/{id}/attachment-permission", (string id) =>
        { using var db = Store(); db.RevokeAttachmentPermission(id); return Results.NoContent(); });
        local.MapGet("/storage", () => { using var db = Store(); return db.PeerStorage(); });
        local.MapPost("/storage/cleanup", () => { using var db = Store(); db.CleanPeerFiles(); return db.PeerStorage(); });
        local.MapGet("", () => { using var db = Store(); return db.FederationSummary(); });
        local.MapGet("/node", () => { using var db = Store(); return network.Describe(db); });
        local.MapGet("/signaling", async (CancellationToken ct) =>
        {
            var state = signaling.Read();
            var service = state.Values.Trust();
            return new { origin = service?.Origin.GetLeftPart(UriPartial.Authority), enabled = rtc.HasBridge && service != null,
                canShare = rtc.ShareBlockReason(state) == null,
                connected = service != null && await service.Check(ct) };
        });
        var signalingRoutes = local.MapGroup("/signaling/settings").AddEndpointFilter((context, next) =>
            WorkbenchHost.IsLoopback(context.HttpContext) ? next(context) : ValueTask.FromResult<object?>(Results.StatusCode(403)));
        signalingRoutes.MapGet("", () => SignalingView(signaling.Read()));
        signalingRoutes.MapPut("", (SignalingSettingsInput input) => SignalingView(signaling.Save(input)));
        local.MapPut("/node", (NodeSettings input) =>
        {
            using var db = Store();
            return network.Configure(db, input);
        });
        local.MapGet("/windows/{id}", (string id) => { using var db = Store(); return network.ShareLink(db, id, rtc.ShareBlockReason(signaling.Read())); });
        local.MapPut("/windows/{id}", async (string id, ShareSettings input, Workbench workbench, CancellationToken ct) =>
        {
            using var db = Store();
            if (input.Enabled && (db.FederationNode().NodeName == null || network.ActiveIp == null && !rtc.Enabled))
                throw new WorkbenchError("请在节点设置中选择可用网卡，并重启工作台启用监听");
            if (input.Enabled) network.Certificate();
            bool restarted = await workbench.ConfigureSharing(id, input, ct);
            return network.ShareLink(db, id, rtc.ShareBlockReason(signaling.Read()), restarted);
        });
        local.MapPut("/grants/{id}", (string id, PeerDecision input) => { using var db = Store(); db.DecidePeer(id, input); return Results.Ok(); });
        local.MapPost("/grants/{id}/review-shown", (string id) =>
        {
            using var db = Store();
            db.ReviewDialogShown(id);
            return Results.NoContent();
        });
        local.MapPost("/windows/{id}/signaling-invitation", async (string id, CancellationToken ct) =>
            new { link = await rtc.Invite(WorkbenchInput.Id(id), ct) });
        local.MapPost("/remotes", async (RemoteApplication input, CancellationToken ct) =>
        {
            if (input.Link?.Length <= 1500 && Uri.TryCreate(input.Link, UriKind.Absolute, out var uri) &&
                uri.AbsolutePath.StartsWith("/signal/", StringComparison.Ordinal))
                return new { id = await rtc.Join(input, uri, ct) };
            using var db = Store();
            return new { id = FederationNetwork.AddRemote(db, input) };
        });
        local.MapDelete("/windows/{window}/contacts/{id}", (string window,string id) =>
        {
            using var db = Store();
            db.RemoveReturnContact(WorkbenchInput.Id(window),WorkbenchInput.Id(id));
            events.RefreshPages();
            return Results.NoContent();
        });
        local.MapPut("/remotes/{id}/contact", (string id, ContactBinding input) =>
        {
            using var db = Store();
            db.SetContactBinding(id,input);
            events.RefreshPages();
            return Results.NoContent();
        });
        local.MapDelete("/remotes/{id}", (string id, string topic, Workbench workbench) =>
        {
            using var db = Store();
            if (db.RemoveRemote(id, WorkbenchInput.Id(topic))) workbench.SyncTeamRoster(topic);
            events.Changed();
            return Results.NoContent();
        });
        local.MapPost("/remotes/{id}/messages", (string id, PeerMessage input, FederationClient client, CancellationToken ct) => client.Send(id, input, ct));
        local.MapPost("/groups", async (GroupLaunch input,CancellationToken ct)=>
        {
            if (input.Selection?.Policy is null) throw new WorkbenchError("请选择常驻协作的成员及权限");
            var application=new RemoteApplication(input.TopicId,input.Link,input.Purpose);
            string id;
            if (application.Link?.Length<=1500 && Uri.TryCreate(application.Link,UriKind.Absolute,out var uri) &&
                uri.AbsolutePath.StartsWith("/signal/",StringComparison.Ordinal))
                id=await rtc.Join(application,uri,ct,input.Selection);
            else
            {
                using var db=Store();
                id=FederationNetwork.AddRemote(db,application,input.Selection);
            }
            events.Changed();
            return new { id };
        });
        local.MapPut("/groups/{id}",(string id,GroupSelection selection)=>
        {
            using var db=Store();
            db.DecideGroupCollaboration(WorkbenchInput.Id(id),selection);
            events.Changed();
            return Results.NoContent();
        });

        var wire = app.MapGroup("/api/federation").WithMetadata(this);
        wire.MapPut("/contacts", (HttpContext c, AdvertisedContact[] contacts) =>
        {
            using var db = Store();
            db.SyncContacts(Token(c),Node(c),contacts);
            events.RefreshPages();
            return Results.NoContent();
        });
        wire.MapPost("/uploads", (Func<HttpContext, Task<IResult>>)(c => Upload(c, false))).WithMetadata(new UploadBody(false));
        wire.MapPost("/attachment-uploads", (Func<HttpContext, Task<IResult>>)(c => Upload(c, true))).WithMetadata(new UploadBody(true));
        wire.MapGet("/documents/{id}", (HttpContext c, string id) =>
        {
            using var db = Store(); var file = db.PeerReplyFile(Token(c), Node(c), id);
            return Results.File(file.Bytes, "text/markdown", file.Name);
        });
        wire.MapPost("/applications", (HttpContext c, PeerApplication input) =>
        {
            using var db = Store();
            var reply = db.ApplyPeer(Token(c), Node(c), Name(c), c.Connection.RemoteIpAddress!.ToString(), input);
            events.Changed();
            return reply;
        });
        wire.MapGet("/application", (HttpContext c) => { using var db = Store(); return db.ClaimPeer(Token(c), Node(c)); });
        wire.MapPost("/messages", (HttpContext c, PeerMessage input) =>
        {
            using var db = Store();
            return db.AcceptPeerMessage(Token(c), Node(c), Name(c), input, events.Changed);
        });
        wire.MapPost("/chat", (HttpContext c,PeerHumanChat input) =>
        {
            using var db = Store();
            var result = db.AcceptHumanChat(Token(c),Node(c),input);
            events.Changed();
            return result;
        });
        wire.MapPost("/chat/{id}/received", (HttpContext c,string id,PeerHumanReceipt input) =>
        {
            using var db = Store();
            db.ConfirmHumanChat(Token(c),Node(c),id,input.Delivered);
            events.Changed();
            return new { state = "delivered" };
        });
        wire.MapGet("/events", Stream);
        wire.MapPost("/groups", (HttpContext c,GroupApplication input)=>
        {
            using var db=Store();
            var result=db.ApplyGroupCollaboration(Token(c),Node(c),Name(c),c.Connection.RemoteIpAddress!.ToString(),input);
            events.Changed();
            return result;
        });
        wire.MapGet("/groups/{id}/application",(HttpContext c,string id)=>
        {
            using var db=Store();
            return db.ClaimGroupCollaboration(id,Token(c),Node(c));
        });
        wire.MapPost("/groups/{id}/withdraw",(HttpContext c,string id)=>
        {
            using var db=Store();
            var session=db.ClaimGroupCollaboration(id,Token(c),Node(c),withdraw:true);
            events.Changed();
            return session;
        });
        wire.MapPost("/groups/{id}/exchange",(HttpContext c,string id,GroupExchange input)=>
        {
            using var db=Store();
            var result=db.ExchangeGroup(id,Token(c),Node(c),Name(c),input,out bool changed);
            if (changed) events.Changed();
            return result;
        }).WithMetadata(new GroupExchangeBody());
        wire.MapGet("/groups/{id}/files/{file}",(HttpContext c,string id,string file)=>
        {
            using var db=Store();
            var item=db.GroupOutboundFile(id,Token(c),Node(c),file);
            return Results.File(item.Bytes,"application/octet-stream",item.Name);
        });
        wire.MapPost("/groups/{id}/files",(Func<HttpContext,string,Task<IResult>>)UploadGroupFile)
            .WithMetadata(new UploadBody(true));
    }

    private sealed record UploadBody(bool Attachments);
    private sealed record GroupExchangeBody;
    private sealed record GroupLaunch(string TopicId,string Link,string Purpose,GroupSelection Selection);
    private async Task<IResult> UploadGroupFile(HttpContext c,string id)
    {
        using var db=Store();
        string token=Token(c), node=Node(c);
        string name=WorkbenchStore.SafePeerName(Uri.UnescapeDataString(c.Request.Headers["X-FF-File-Name"].ToString()));
        string? Actor(string field)
        {
            var value=c.Request.Headers[field];
            return value.Count==0?null:WorkbenchInput.Id(value.ToString());
        }
        var route=new GroupFileRoute(WorkbenchInput.Id(c.Request.Headers["X-FF-Group-Message"].ToString()),
            Actor("X-FF-Group-Sender"),Actor("X-FF-Group-Recipient"));
        void Check() => db.CheckGroupUpload(id,token,node,route);
        Check();
        var bytes=await WorkbenchStore.PeerAttachmentBytes(c.Request.Body,name,c.RequestAborted);
        var receipt=db.SavePeerFile(id,node,c.Request.Headers["X-FF-Upload-Key"].ToString(),name,bytes,Check);
        db.StageGroupUpload(id,route,receipt.UploadId);
        return Results.Ok(receipt);
    }
    private sealed record AttachmentDecision(string Action);
    private async Task<IResult> Upload(HttpContext c, bool attachments)
    {
        using var db = Store();
        var token = Token(c); var node = Node(c);
        var owner = db.UploadOwner(token, node);
        string filename = Uri.UnescapeDataString(c.Request.Headers["X-FF-File-Name"].ToString());
        if (!attachments && !filename.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            throw new FederationException(415, "markdown_required");
        string safe = WorkbenchStore.SafePeerName(filename);
        var bytes = await WorkbenchStore.PeerAttachmentBytes(c.Request.Body, safe, c.RequestAborted);
        try
        {
            var receipt = db.SavePeerFile(owner, node, c.Request.Headers["X-FF-Upload-Key"].FirstOrDefault() ?? Guid.NewGuid().ToString("N"),
                safe, bytes, () => db.UploadOwner(token, node));
            return Results.Ok(receipt);
        }
        catch (FederationException e) when (e.Status is 429 or 507)
        {
            db.UploadBlocked(owner, e.Message);
            events.RefreshPages();
            throw;
        }
    }

    private async Task Stream(HttpContext c)
    {
        long cursor = 0;
        if (c.Request.Headers.TryGetValue("Last-Event-ID", out var last) && (!long.TryParse(last, out cursor) || cursor < 0))
            throw new FederationException(400, "invalid_cursor");
        string grant;
        using (var db = Store()) grant = db.AuthorizePeer(Token(c), Node(c));
        if (!streams.Wait(0)) throw new FederationException(429, "stream_limit");
        bool attached = false;
        try
        {
            using (var db = Store()) attached = db.OpenPeerStream(grant);
            if (!attached) throw new FederationException(429, "stream_limit");
            events.RefreshPages();
            c.Response.ContentType = "text/event-stream";
            c.Response.Headers.CacheControl = "no-store";
            bool acceptsContacts = c.Request.Headers["X-FF-Return-Contacts"] == "1";
            bool acceptsRemoval = c.Request.Headers["X-FF-Contact-Removal"] == "1";
            bool acceptsHumanChat = c.Request.Headers["X-FF-Human-Chat"] == "1";
            if (acceptsContacts) c.Response.Headers["X-FF-Return-Contacts"] = "1";
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(c.RequestAborted,
                c.RequestServices.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>().ApplicationStopping);
            var ct = stop.Token;
            while (!ct.IsCancellationRequested && (RtcListener(c) || network.AdmitsPeer(c.Connection.RemoteIpAddress!, c.Request.Host)))
            {
                PeerSession state;
                List<PeerEvent> items;
                using (var db = Store())
                {
                    // 吊销、关闭共享、移除窗口对已连接的 SSE 同样生效。
                    try { state = db.PeerStatus(Token(c), Node(c)); }
                    catch (Exception e) when (e is FederationException or WorkbenchError) { return; }
                    items = db.PeerEvents(state.Id, cursor, acceptsContacts,acceptsRemoval,acceptsHumanChat);
                }
                await c.Response.WriteAsync("data: " + JsonSerializer.Serialize(new { state }, Json) + "\n\n", ct);
                foreach (var item in items)
                {
                    await c.Response.WriteAsync($"id: {item.Id}\ndata: " + JsonSerializer.Serialize(new { item }, Json) + "\n\n", ct);
                    cursor = item.Id;
                }
                await c.Response.Body.FlushAsync(ct);
                if (items.Count < 32) await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
        finally
        {
            streams.Release();
            if (attached)
            {
                using var db = Store();
                db.ClosePeerStream(grant);
                events.RefreshPages();
            }
        }
    }
    public void Dispose() { limiter.Dispose(); streams.Dispose(); }
}

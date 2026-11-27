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

internal sealed class FederationEndpoints(WorkbenchStartup startup, FederationNetwork network, WorkbenchEvents events) : IDisposable
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

    // 使用已匹配端点的标记划分网络面，未知路由、静态资源、终端升级请求都走本机边界。
    internal bool Admit(HttpContext c)
    {
        if (c.GetEndpoint()?.Metadata.GetMetadata<FederationEndpoints>() == null) return false;
        if (!c.Request.IsHttps || !Equals(c.Connection.LocalIpAddress, network.ActiveIp) ||
            c.Connection.RemoteIpAddress is not { } ip || !network.AdmitsPeer(ip, c.Request.Host) ||
            c.Request.Headers.ContainsKey("Origin")) throw new FederationException(403, "lan_only");
        using var lease = limiter.AttemptAcquire(ip.ToString());
        if (!lease.IsAcquired) throw new FederationException(429, "rate_limited");
        string auth = c.Request.Headers.Authorization.ToString();
        if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || !FederationPolicy.IsSecret(auth[7..]))
            throw new FederationException(401, "bearer_required");
        c.Items["peerToken"] = auth[7..];
        c.Items["peerNode"] = WorkbenchInput.Id(c.Request.Headers["X-FF-Node-Id"]);
        c.Items["peerName"] = FederationPolicy.Name(Uri.UnescapeDataString(c.Request.Headers["X-FF-Node-Name"].ToString()));
        int bodyLimit = c.GetEndpoint()?.Metadata.GetMetadata<UploadBody>() != null ? WorkbenchStore.PeerFileLimit : 65536;
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
        local.MapGet("/storage", () => { using var db = Store(); return db.PeerStorage(); });
        local.MapPost("/storage/cleanup", () => { using var db = Store(); db.CleanPeerFiles(); return db.PeerStorage(); });
        local.MapGet("", () => { using var db = Store(); return db.FederationSummary(); });
        local.MapGet("/node", () => { using var db = Store(); return network.Describe(db); });
        local.MapPut("/node", (NodeSettings input) =>
        {
            using var db = Store();
            return network.Configure(db, input);
        });
        local.MapGet("/windows/{id}", (string id) => { using var db = Store(); return network.ShareLink(db, id); });
        local.MapPut("/windows/{id}", async (string id, ShareSettings input, Workbench workbench, CancellationToken ct) =>
        {
            using var db = Store();
            if (input.Enabled && (db.FederationNode().NodeName == null || network.ActiveIp == null))
                throw new WorkbenchError("请在节点设置中选择可用网卡，并重启工作台启用监听");
            if (input.Enabled) network.Certificate();
            bool restarted = await workbench.ConfigureSharing(id, input, ct);
            return network.ShareLink(db, id, restarted);
        });
        local.MapPut("/grants/{id}", (string id, PeerDecision input) => { using var db = Store(); db.DecidePeer(id, input); return Results.Ok(); });
        local.MapGet("/grants/{id}/requests", (string id) => { using var db = Store(); return db.RecentPeerRequests(id); });
        local.MapPost("/grants/{id}/replies", (string id, PeerMessage input) => { using var db = Store(); db.PeerReply(id, input); return Results.Ok(); });
        local.MapPost("/remotes", (RemoteApplication input) => { using var db = Store(); return new { id = FederationNetwork.AddRemote(db, input) }; });
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

        var wire = app.MapGroup("/api/federation").WithMetadata(this);
        wire.MapPut("/contacts", (HttpContext c, AdvertisedContact[] contacts) =>
        {
            using var db = Store();
            db.SyncContacts(Token(c),Node(c),contacts);
            events.RefreshPages();
            return Results.NoContent();
        });
        wire.MapPost("/uploads", (Func<HttpContext, Task<IResult>>)Upload).WithMetadata(new UploadBody());
        wire.MapGet("/documents/{id}", (HttpContext c, string id) =>
        {
            using var db = Store(); var file = db.PeerReplyFile(Token(c), Node(c), id);
            return Results.File(file.Bytes, "text/markdown", file.Name);
        });
        wire.MapPost("/applications", (HttpContext c, PeerApplication input) =>
        {
            using var db = Store();
            var reply = db.ApplyPeer(Token(c), Node(c), Name(c), c.Connection.RemoteIpAddress!.ToString(), input);
            events.RefreshPages();
            return reply;
        });
        wire.MapGet("/application", (HttpContext c) => { using var db = Store(); return db.ClaimPeer(Token(c), Node(c)); });
        wire.MapPost("/messages", (HttpContext c, PeerMessage input) =>
        {
            using var db = Store();
            return db.AcceptPeerMessage(Token(c), Node(c), Name(c), input, events.Changed);
        });
        wire.MapGet("/events", Stream);
    }

    private sealed class UploadBody;
    private async Task<IResult> Upload(HttpContext c)
    {
        using var db = Store();
        var token = Token(c); var node = Node(c);
        var owner = db.UploadOwner(token, node);
        var bytes = await WorkbenchStore.PeerBytes(c.Request.Body, c.RequestAborted);
        try
        {
            var receipt = db.SavePeerFile(owner, node, c.Request.Headers["X-FF-Upload-Key"].FirstOrDefault() ?? Guid.NewGuid().ToString("N"),
                Uri.UnescapeDataString(c.Request.Headers["X-FF-File-Name"].ToString()), bytes, () => db.UploadOwner(token, node));
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
            if (acceptsContacts) c.Response.Headers["X-FF-Return-Contacts"] = "1";
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(c.RequestAborted,
                c.RequestServices.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>().ApplicationStopping);
            var ct = stop.Token;
            while (!ct.IsCancellationRequested && network.AdmitsPeer(c.Connection.RemoteIpAddress!, c.Request.Host))
            {
                PeerSession state;
                List<PeerEvent> items;
                using (var db = Store())
                {
                    // 吊销、关闭共享、移除窗口对已连接的 SSE 同样生效。
                    try { state = db.PeerStatus(Token(c), Node(c)); }
                    catch (Exception e) when (e is FederationException or WorkbenchError) { return; }
                    items = db.PeerEvents(state.Id, cursor, acceptsContacts,acceptsRemoval);
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

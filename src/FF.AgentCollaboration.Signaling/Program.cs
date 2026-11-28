using System.Net;
using FF.AgentCollaboration.Signaling;
using FF.AgentCollaboration.Updates;

if(args is ["signaling-managed",..])
{
    Environment.ExitCode=await UpdateSupervisor.Run("signaling",args[1..]);
    return;
}
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{ Args=args,ContentRootPath=UpdateSupervisor.InstallHome??Directory.GetCurrentDirectory() });
if (builder.Configuration["urls"] is null)
    builder.WebHost.UseUrls("http://127.0.0.1:5190");

string? key = builder.Configuration["InternalService:Key"];
if (string.IsNullOrWhiteSpace(key))
    throw new InvalidOperationException("Required configuration: InternalService:Key");
if (!Uri.TryCreate(builder.Configuration["Signaling:PublicOrigin"], UriKind.Absolute, out var origin) ||
    origin.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(origin.UserInfo) || origin.AbsolutePath != "/" ||
    !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment) || origin.Port == 0)
    throw new InvalidOperationException("Signaling:PublicOrigin must be a public HTTPS origin, such as https://signal.example.com");

string? certificatePath = builder.Configuration["Signaling:CertificatePath"];
using var certificate = string.IsNullOrWhiteSpace(certificatePath) ? null :
    new SignalCertificate(Path.GetFullPath(certificatePath, builder.Environment.ContentRootPath), ParseAddress(origin));
builder.WebHost.ConfigureKestrel(server =>
{
    server.Limits.MaxRequestBodySize = 4096;
    if (certificate != null)
        server.ConfigureHttpsDefaults(https => https.ServerCertificateSelector = (_, _) => certificate.Current);
});

builder.Services.AddSingleton(sp => new StunBindingService(origin, sp.GetRequiredService<ILogger<StunBindingService>>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<StunBindingService>());
byte[] issuerHash = InvitationRooms.Digest(key);
var app = builder.Build();
var version=UpdateSupervisor.Version(typeof(InvitationRooms).Assembly);
var updates=new ManagedUpdates("signaling",version,builder.Configuration,app.Lifetime);
var helper = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows()
    ? "FF.AgentCollaboration.WebRtc.exe" : "FF.AgentCollaboration.WebRtc");
var rooms = new InvitationRooms(origin, TimeProvider.System, File.Exists(helper) ? helper : null,
    app.Services.GetRequiredService<StunBindingService>(),
    Path.GetFullPath(builder.Configuration["Signaling:PairingsPath"] ?? "data/signaling-pairings.json",builder.Environment.ContentRootPath));
if (!File.Exists(helper)) app.Logger.LogWarning("缺少 WebRTC 组件，内置 TURN 中继不可用");
app.Use(async (context, next) =>
{
    // 公网可以直连 TLS，反向代理可在同主机通过环回 HTTP 转发。
    if (!context.Request.IsHttps && !(context.Connection.LocalIpAddress is { } local &&
        context.Connection.RemoteIpAddress is { } remote && IPAddress.IsLoopback(local) && IPAddress.IsLoopback(remote)))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    context.Response.Headers.CacheControl = "no-store";
    await next(context);
});
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(25),
    KeepAliveTimeout = TimeSpan.FromSeconds(12)
});

var api = app.MapGroup("/api/signaling/v1");
api.MapGet("/status", () => Results.NoContent());
api.MapGet("/updates",async (HttpRequest request,bool? progress,CancellationToken ct)=>
    progress==true || !ManagedUpdates.AuthorizeAdmin(builder.Configuration,SignalingSocket.Bearer(request))
        ? updates.Status : await updates.Check(ct));
api.MapPost("/updates",(HttpRequest request,UpdateRequest input)=>
{
    if(request.Headers.ContainsKey("Origin") ||
        !ManagedUpdates.AuthorizeAdmin(builder.Configuration,SignalingSocket.Bearer(request))) return Results.Unauthorized();
    try
    {
        var id=updates.Schedule(input with {Mode="now",Tasks=null},()=>true,(_,_)=>{});
        return Results.Accepted(value:new {id});
    }
    catch(UpdateFailure error){return Results.Conflict(new {error=error.Message});}
});
api.MapPost("/invitations", async (HttpContext context) =>
{
    if (!context.Request.Headers.TryGetValue("X-Lx-Service-Key", out var issuedBy) || issuedBy.Count != 1 ||
        !InvitationRooms.Matches(issuerHash, issuedBy[0]))
        return Results.Unauthorized();
    InvitationTarget? target;
    try { target = await context.Request.ReadFromJsonAsync<InvitationTarget>(context.RequestAborted); }
    catch (System.Text.Json.JsonException) { return Results.BadRequest(); }
    catch (InvalidOperationException) { return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType); }
    if (target is not { } ||
        !Guid.TryParseExact(target.NodeId, "N", out var node) || node == Guid.Empty ||
        !Guid.TryParseExact(target.WindowId, "N", out var window) || window == Guid.Empty ||
        !InvitationRooms.IsToken(target.Pin))
        return Results.BadRequest();

    var invite = rooms.Issue(target);
    return invite is null ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable) :
        Results.Json(invite, statusCode: StatusCodes.Status201Created);
});
api.MapPost("/rooms/{id:guid}/claim", async (Guid id, HttpContext context) =>
{
    string? node=null;
    if(context.Request.HasJsonContentType())
    {
        var input=await context.Request.ReadFromJsonAsync<ClaimRoom>(context.RequestAborted);
        if(input?.NodeId is string claimedNode)
        {
            if(!Guid.TryParseExact(claimedNode,"N",out var nodeId) || nodeId==Guid.Empty) return Results.BadRequest();
            node=nodeId.ToString("N");
        }
    }
    var claimed = rooms.Claim(id, SignalingSocket.Bearer(context.Request),node);
    return claimed is null ? Results.Unauthorized() : Results.Ok(claimed);
});
api.MapPost("/rooms/{id:guid}/pair", (Guid id, PairRoom input, HttpRequest request) =>
    rooms.Approve(id,SignalingSocket.Bearer(request),input) ? Results.NoContent() : Results.Unauthorized());
api.MapPost("/rooms/{id:guid}/renew", (Guid id,HttpContext context) =>
{
    var (ticket,closed)=rooms.Renew(id,SignalingSocket.Bearer(context.Request));
    if(ticket!=null)return Results.Ok(ticket);
    if(!closed.HasValue)return Results.Unauthorized();
    context.Response.Headers["X-FF-Rtc-Closed-By"]=closed.Value?"host":"guest";
    return Results.StatusCode(410);
});
api.MapDelete("/rooms/{id:guid}/pair", (Guid id,HttpRequest request) =>
    rooms.Unpair(id,SignalingSocket.Bearer(request)) ? Results.NoContent() : Results.Unauthorized());
api.MapGet("/rooms/{id:guid}/connect", (Guid id, HttpContext context) =>
    SignalingSocket.Handle(context, rooms, id, app.Lifetime.ApplicationStopping));
api.MapGet("/rooms/{id:guid}/relay", (Guid id, HttpContext context) => TurnRoom.Handle(context, rooms, id));

await app.StartAsync();
UpdateSupervisor.Ready(version);
Console.WriteLine("FF_SIGNALING " + app.Urls.First());
if (certificate != null)
    Console.WriteLine("FF_SIGNALING_SPKI_SHA256 " + certificate.Pin);
var renewal = certificate?.Maintain(app.Logger, app.Lifetime.ApplicationStopping);
await app.WaitForShutdownAsync();
if (renewal != null) await renewal;

static IPAddress ParseAddress(Uri origin) => IPAddress.TryParse(origin.IdnHost.Trim('[', ']'), out var address) &&
    !IPAddress.Any.Equals(address) && !IPAddress.IPv6Any.Equals(address)
    ? address : throw new InvalidOperationException("Automatic signaling certificates require an IP address in Signaling:PublicOrigin");

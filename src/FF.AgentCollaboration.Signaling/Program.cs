using System.Net;
using FF.AgentCollaboration.Signaling;

var builder = WebApplication.CreateBuilder(args);
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
var helper = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows()
    ? "FF.AgentCollaboration.WebRtc.exe" : "FF.AgentCollaboration.WebRtc");
var rooms = new InvitationRooms(origin, TimeProvider.System, File.Exists(helper) ? helper : null,
    app.Services.GetRequiredService<StunBindingService>());
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
api.MapPost("/rooms/{id:guid}/claim", (Guid id, HttpContext context) =>
{
    var claimed = rooms.Claim(id, SignalingSocket.Bearer(context.Request));
    return claimed is null ? Results.Unauthorized() : Results.Ok(claimed);
});
api.MapGet("/rooms/{id:guid}/connect", (Guid id, HttpContext context) =>
    SignalingSocket.Handle(context, rooms, id, app.Lifetime.ApplicationStopping));
api.MapGet("/rooms/{id:guid}/relay", (Guid id, HttpContext context) => TurnRoom.Handle(context, rooms, id));

await app.StartAsync();
Console.WriteLine("FF_SIGNALING " + app.Urls.First());
if (certificate != null)
    Console.WriteLine("FF_SIGNALING_SPKI_SHA256 " + certificate.Pin);
var renewal = certificate?.Maintain(app.Logger, app.Lifetime.ApplicationStopping);
await app.WaitForShutdownAsync();
if (renewal != null) await renewal;

static IPAddress ParseAddress(Uri origin) => IPAddress.TryParse(origin.IdnHost.Trim('[', ']'), out var address) &&
    !IPAddress.Any.Equals(address) && !IPAddress.IPv6Any.Equals(address)
    ? address : throw new InvalidOperationException("Automatic signaling certificates require an IP address in Signaling:PublicOrigin");

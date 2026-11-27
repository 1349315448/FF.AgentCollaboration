using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using FF.AgentCollaboration.Application;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Files;
using FF.AgentCollaboration.Infrastructure.Storage;

namespace FF.AgentCollaboration.Presentation.Http;

internal static class WorkbenchHost
{
    internal static bool IsLoopback(HttpContext context) =>
        context.Connection.LocalIpAddress is { } local && IPAddress.IsLoopback(local) &&
        context.Connection.RemoteIpAddress is { } remote && IPAddress.IsLoopback(remote);

    internal static async Task Run(string root, string[] options)
    {
        string? project = null;
        string? handoffs = null;
        string? knowledge = null;
        int port = 5186;
        int? lanPort = null;
        bool fixedDirectory = false;
        bool openBrowser = false;
        bool loopbackOnly = false;
        for (int index = 0; index < options.Length; index++)
        {
            if (options[index] == "--open-browser") { openBrowser = true; continue; }
            if (options[index] == "--fixed-data-dir") { fixedDirectory = true; continue; }
            if (options[index] == "--loopback-only") { loopbackOnly = true; continue; }
            if (index + 1 == options.Length) throw new WorkbenchError($"请为 {options[index]} 指定参数");
            switch (options[index])
            {
                case "--project": project = ProjectFolders.Resolve(options[index + 1]); break;
                case "--handoffs": handoffs = Path.GetFullPath(options[index + 1]); break;
                case "--knowledge":
                    string path = Path.GetFullPath(options[index + 1]);
                    knowledge = Directory.Exists(path) ? ProjectFolders.Resolve(path) : null;
                    break;
                case "--port" when int.TryParse(options[index + 1], out port) && port is >= 0 and <= 65535: break;
                case "--lan-http-port" when int.TryParse(options[index + 1], out int lan) && lan is > 0 and <= 65535: lanPort = lan; break;
                default: throw new WorkbenchError($"无效的工作台参数 {options[index]}");
            }
            index++;
        }

        if (loopbackOnly && lanPort != null) throw new WorkbenchError("仅本机监听不能同时开启局域网 HTTP");
        CollaborationSkill.Read();
        CollaborationSkill.ReadTeam(leader: true);
        CollaborationSkill.ReadTeam(leader: false);
        string initial = root;
        var location = new WorkbenchLocation(initial, fixedDirectory, knowledge);
        using var startup = new WorkbenchStartup(location, initial, project, knowledge, handoffs);
        startup.OpenExisting();
        using var network = new FederationNetwork(startup);
        network.SelectListener(port, loopbackOnly);
        IPAddress? lanIp = null;
        if (lanPort != null)
        {
            if (lanPort == network.Port) throw new WorkbenchError("局域网 HTTP 端口须与工作台及联邦端口不同");
            lanIp = network.ActiveIp is {} active && FederationNetwork.PrivateIp(active) ? active :
                FederationNetwork.Addresses().FirstOrDefault(FederationNetwork.PrivateIp) ?? throw new WorkbenchError("局域网 HTTP 工作台需要本机私网 IPv4 地址");
        }
        using var lanAccess = new LanWorkbenchAccess(lanIp, lanPort ?? 0);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = "wwwroot"
        });
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.Listen(IPAddress.Loopback, network.Port);
            if (lanIp != null) server.Listen(lanIp, lanPort!.Value);
            if (network.ActiveIp != null)
                server.Listen(network.ActiveIp, network.Port, listener => listener.UseHttps(tls =>
                    tls.ServerCertificateSelector = (_, _) => network.Certificate()));
            server.Limits.MaxRequestBodySize = 300_000;
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        var events = new WorkbenchEvents();
        builder.Services.AddSingleton(_ => new FederationClient(startup, events));
        builder.Services.AddHostedService(services => services.GetRequiredService<FederationClient>());
        builder.Services.AddSingleton(_ => new SessionCatalog(startup));
        builder.Services.AddSingleton(services => new Workbench(startup, services.GetRequiredService<SessionCatalog>(), services.GetRequiredService<ILogger<Workbench>>(), events));
        builder.Services.AddHostedService(services => services.GetRequiredService<Workbench>());
        await using var app = builder.Build();
        using var federation = new FederationEndpoints(startup, network, events);
        app.UseRouting();
        app.Use(lanAccess.Invoke);

        app.Use(async (context, next) =>
        {
            // 环回监听与 Host/Origin 一起约束网站访问本机项目及引擎的入口。
            var request = context.Request;
            bool pairedLan = lanAccess.Matches(context);
            string uiHost = pairedLan ? request.Host.ToUriComponent() : $"127.0.0.1:{context.Connection.LocalPort}";
            string expectedOrigin = $"http://{uiHost}";
            try
            {
                if (federation.Admit(context))
                {
                    context.Response.Headers.CacheControl = "no-store";
                    await next(context);
                    return;
                }
                if (!pairedLan && (!Equals(context.Connection.LocalIpAddress, IPAddress.Loopback) ||
                    context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote) ||
                    request.Host.Host != "127.0.0.1" || request.Host.Port != context.Connection.LocalPort ||
                    (request.Headers.TryGetValue("Origin", out var origin) && origin != expectedOrigin)))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }

                context.Response.Headers.ContentSecurityPolicy =
                    $"default-src 'self'; style-src 'self' 'unsafe-inline'; connect-src 'self' ws://{uiHost}; img-src 'self' blob:; frame-src 'self'; frame-ancestors 'none'; object-src 'none'; base-uri 'none'";
                if (request.Path.StartsWithSegments("/api")) context.Response.Headers.CacheControl = "no-store";
                bool mutation = request.Path.StartsWithSegments("/api") && request.Method is "POST" or "PUT" or "DELETE";
                if (mutation && request.Headers.TryGetValue("X-FF-View", out var view) &&
                    (!Guid.TryParse(view, out var owner) || events.PageClosed(owner).IsCancellationRequested))
                {
                    await Results.Json(new { error = "工作台已在新页面打开" }, statusCode: 409).ExecuteAsync(context);
                    return;
                }
                if (!startup.Ready.IsCompletedSuccessfully && request.Path.StartsWithSegments("/api") &&
                    request.Path != "/api/initialize" && request.Path != "/api/folders" &&
                    request.Path != "/api/page" && !(request.Path == "/api/events" && request.Query.ContainsKey("page")))
                {
                    await Results.Json(new { error = "请先完成工作台初始化" }, statusCode: 409).ExecuteAsync(context);
                    return;
                }
                await next(context);
                if (mutation && request.Path != "/api/page" && context.Response.StatusCode is >= 200 and < 300) events.Changed();
            }
            catch (FederationException error) when (!context.Response.HasStarted)
            {
                await Results.Json(error.Payload, statusCode: error.Status).ExecuteAsync(context);
            }
            catch (BadHttpRequestException error) when (!context.Response.HasStarted)
            {
                await Results.Json(new { error = "请求内容无效或过大" }, statusCode: error.StatusCode).ExecuteAsync(context);
            }
            catch (WorkbenchError error) when (!context.Response.HasStarted)
            {
                await Results.Json(new { error = error.Message }, statusCode: HttpErrors.Status(error.Failure)).ExecuteAsync(context);
            }
            catch (Exception error) when (!context.Response.HasStarted)
            {
                if (request.HttpContext.RequestAborted.IsCancellationRequested) return;
                app.Logger.LogWarning("{Path} failed: {Type}", request.Path, error.GetType().Name);
                await Results.Json(new { error = HttpErrors.Error(error) }, statusCode: 503).ExecuteAsync(context);
            }
        });
        app.UseWebSockets();
        app.Use(async (context, next) =>
        {
            if (context.WebSockets.IsWebSocketRequest && context.Request.Query.TryGetValue("page", out var value))
            {
                var closed = Guid.TryParse(value, out var id) ? events.PageClosed(id) : new CancellationToken(true);
                if (closed.IsCancellationRequested)
                {
                    context.Response.StatusCode = StatusCodes.Status409Conflict;
                    return;
                }
                // 冻结的浏览器可能暂时收不到 SSE，宿主仍立即分离其终端查看器。
                using var registration = closed.Register(context.Abort);
                await next(context);
                return;
            }
            await next(context);
        });
        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.MapPost("/api/page", (HttpRequest request) =>
            request.Headers["X-FF-Page"] == "open" ? Results.Json(events.OpenPage()) : Results.StatusCode(403));
        app.MapGet("/api/events", (Guid? page) => TypedResults.ServerSentEvents(events.Subscribe(app.Lifetime.ApplicationStopping, page)));
        // MCP 只提示重新读取已提交数据。该入口没有消息正文、Grant 或直接写终端的能力。
        app.MapPost("/api/events/wake", (HttpRequest request) =>
            request.Headers["X-FF-Notify"] == "1" ? Results.NoContent() : Results.StatusCode(403));
        WorkbenchEndpoints.Map(app, startup, location);
        lanAccess.Map(app);
        federation.Map(app);
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            try { location.RefreshPendingBackup(); }
            catch (Exception error) { app.Logger.LogWarning(error, "关闭时备份失败，下次启动将从原目录重新同步"); }
        });
        await app.StartAsync();
        string address = app.Urls.Single(url => new Uri(url).Host == "127.0.0.1");
        network.Port = new Uri(address).Port;
        events.NotifyPort = new Uri(address).Port;
        Console.WriteLine($"LX_WORKBENCH {address}");
        if (openBrowser)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(address) { UseShellExecute = true }); }
            catch (System.ComponentModel.Win32Exception) { Console.Error.WriteLine("请在浏览器中打开上面的工作台地址。"); }
        }
        await app.WaitForShutdownAsync();
    }
}

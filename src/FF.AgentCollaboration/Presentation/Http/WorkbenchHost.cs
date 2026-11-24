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
    internal static async Task Run(string root, string[] options)
    {
        string? project = null;
        string? handoffs = null;
        string? knowledge = null;
        int port = 5186;
        bool fixedDirectory = false;
        bool openBrowser = false;
        for (int index = 0; index < options.Length; index++)
        {
            if (options[index] == "--open-browser") { openBrowser = true; continue; }
            if (options[index] == "--fixed-data-dir") { fixedDirectory = true; continue; }
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
                default: throw new WorkbenchError($"无效的工作台参数 {options[index]}");
            }
            index++;
        }

        string initial = root;
        var location = new WorkbenchLocation(initial, fixedDirectory, knowledge);
        using var startup = new WorkbenchStartup(location, initial, project, knowledge, handoffs);
        startup.OpenExisting();
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = "wwwroot"
        });
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.Listen(IPAddress.Loopback, port);
            server.Limits.MaxRequestBodySize = 300_000;
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton(_ => new SessionCatalog(startup));
        builder.Services.AddSingleton(services => new Workbench(startup, services.GetRequiredService<SessionCatalog>(), services.GetRequiredService<ILogger<Workbench>>()));
        builder.Services.AddHostedService(services => services.GetRequiredService<Workbench>());
        await using var app = builder.Build();

        app.Use(async (context, next) =>
        {
            // 环回监听与 Host/Origin 一起约束网站访问本机项目及引擎的入口。
            var request = context.Request;
            string expectedOrigin = $"http://127.0.0.1:{context.Connection.LocalPort}";
            if (request.Host.Host != "127.0.0.1" || request.Host.Port != context.Connection.LocalPort ||
                (request.Headers.TryGetValue("Origin", out var origin) && origin != expectedOrigin))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            context.Response.Headers.ContentSecurityPolicy =
                $"default-src 'self'; style-src 'self' 'unsafe-inline'; connect-src 'self' ws://127.0.0.1:{context.Connection.LocalPort}; frame-ancestors 'none'; object-src 'none'; base-uri 'none'";
            if (request.Path.StartsWithSegments("/api")) context.Response.Headers.CacheControl = "no-store";
            if (!startup.Ready.IsCompletedSuccessfully && request.Path.StartsWithSegments("/api") &&
                request.Path != "/api/initialize" && request.Path != "/api/folders")
            {
                await Results.Json(new { error = "请先完成工作台初始化" }, statusCode: 409).ExecuteAsync(context);
                return;
            }
            try { await next(context); }
            catch (WorkbenchError error)
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
        app.UseDefaultFiles();
        app.UseStaticFiles();

        WorkbenchEndpoints.Map(app, startup, location);
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            try { location.RefreshPendingBackup(); }
            catch (Exception error) { app.Logger.LogWarning(error, "关闭时备份失败，下次启动将从原目录重新同步"); }
        });
        await app.StartAsync();
        string address = app.Urls.Single();
        Console.WriteLine($"LX_WORKBENCH {address}");
        if (openBrowser)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(address) { UseShellExecute = true }); }
            catch (System.ComponentModel.Win32Exception) { Console.Error.WriteLine("请在浏览器中打开上面的工作台地址。"); }
        }
        await app.WaitForShutdownAsync();
    }
}

using System.Net;
using System.Security.Cryptography;
using System.Text;
using FF.AgentCollaboration.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace FF.AgentCollaboration.Presentation.Http;

// 局域网开发入口单独绑定私网网卡。仅环回页面可签发一次性配对码或断开浏览器。
internal sealed class LanWorkbenchAccess(IPAddress? address, int port) : IDisposable
{
    private readonly string cookieName = $"ff-workbench-session-{port}";
    private readonly object gate = new();
    private string? code, cookie;
    private DateTimeOffset expires;
    private IPAddress? client;
    private CancellationTokenSource? session;
    private string Origin => port == 80 ? $"http://{address}" : $"http://{address}:{port}";
    internal bool Matches(HttpContext c) => address != null && c.Connection.LocalPort == port && Equals(c.Connection.LocalIpAddress, address);

    internal void Map(WebApplication app)
    {
        var local = app.MapGroup("/api/lan-access").WithMetadata(this);
        local.MapGet("", () => { lock (gate) return Results.Ok(new { address = address == null ? null : Origin, client = client?.ToString() }); });
        local.MapPost("/pairing", () =>
        {
            if (address == null) return Results.Conflict();
            lock (gate)
            {
                code = RandomNumberGenerator.GetHexString(24);
                expires = DateTimeOffset.UtcNow.AddMinutes(10);
                return Results.Ok(new { address = Origin, code });
            }
        });
        local.MapDelete("/session", () => { Revoke(); return Results.NoContent(); });
    }

    internal async Task Invoke(HttpContext c, RequestDelegate next)
    {
        if (!Matches(c)) { await next(c); return; }
        var r = c.Request;
        var origin = r.Headers.Origin;
        bool needsOrigin = !HttpMethods.IsGet(r.Method) && !HttpMethods.IsHead(r.Method) || r.Headers.ContainsKey("Upgrade");
        if (c.Connection.RemoteIpAddress is not { } source || !FederationNetwork.PrivateIp(source) ||
            r.Host.Host != address!.ToString() || (r.Host.Port ?? 80) != port ||
            ((needsOrigin || origin.Count != 0) && origin != Origin) ||
            c.GetEndpoint()?.Metadata.GetMetadata<LanWorkbenchAccess>() != null)
        {
            c.Response.StatusCode = 403;
            return;
        }
        c.Response.Headers.CacheControl = "no-store";
        c.Response.Headers["Referrer-Policy"] = "same-origin";
        if (r.Path == "/lan-login" && HttpMethods.IsPost(r.Method))
        {
            if (r.ContentType?.Split(';')[0] != "application/x-www-form-urlencoded") { c.Response.StatusCode = 415; return; }
            var limit = c.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = 1024;
            var entered = (await r.ReadFormAsync(c.RequestAborted))["code"].ToString().Trim().ToUpperInvariant();
            if (Pair(c, source, entered)) { c.Response.Redirect("/"); return; }
            c.Response.StatusCode = 401;
            await SignIn(c, "配对码无效或已过期");
            return;
        }
        CancellationToken lifetime;
        lock (gate)
            lifetime = Equals(source, client) && Equal(cookie, r.Cookies[cookieName]) && session != null ?
                session.Token : new CancellationToken(true);
        if (lifetime.IsCancellationRequested)
        {
            if (r.Path == "/" && HttpMethods.IsGet(r.Method)) await SignIn(c, "");
            else c.Response.StatusCode = 401;
            return;
        }
        using var stop = lifetime.Register(c.Abort);
        await next(c);
    }

    private static bool Equal(string? expected, string? supplied) => expected != null && expected.Length == supplied?.Length &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(supplied));

    private bool Pair(HttpContext c, IPAddress source, string entered)
    {
        CancellationTokenSource? previous;
        lock (gate)
        {
            if (DateTimeOffset.UtcNow >= expires || !Equal(code, entered)) return false;
            code = null;
            previous = session;
            session = new();
            client = source;
            cookie = RandomNumberGenerator.GetHexString(64);
            c.Response.Cookies.Append(cookieName, cookie, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/" });
        }
        previous?.Cancel();
        previous?.Dispose();
        return true;
    }

    private void Revoke()
    {
        CancellationTokenSource? previous;
        lock (gate)
        {
            code = cookie = null;
            client = null;
            previous = session;
            session = null;
        }
        previous?.Cancel();
        previous?.Dispose();
    }

    private static Task SignIn(HttpContext c, string error)
    {
        c.Response.ContentType = "text/html; charset=utf-8";
        c.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
        return c.Response.WriteAsync($$"""
            <!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>连接开发工作台</title><style>
            :root{color-scheme:dark;font:15px system-ui;background:#101015;color:#eee}main{max-width:24rem;margin:15vh auto;padding:1.5rem}
            label,input,button{display:block}input,button{padding:.6rem .8rem;border:1px solid #555;border-radius:5px;font:inherit}
            input{box-sizing:border-box;width:100%;margin:.4rem 0 1rem}p{color:#aaa}strong{color:#ffab91}</style>
            <main><h1>连接开发工作台</h1><p>在主机的「工作台配置 → 局域网开发」获取一次性配对码。</p>
            <form method="post" action="/lan-login"><label>配对码<input name="code" autocomplete="off" maxlength="24" required autofocus></label>
            <button>连接工作台</button></form><strong role="alert">{{error}}</strong></main></html>
            """);
    }

    public void Dispose() => Revoke();
}

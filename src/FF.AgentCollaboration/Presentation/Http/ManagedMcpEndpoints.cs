using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using FF.AgentCollaboration.Application;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Engines;
using FF.AgentCollaboration.Infrastructure.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FF.AgentCollaboration.Presentation.Http;

internal sealed record McpImport(JsonElement McpServers,string Scope="global",string? ProjectPath=null,string[]? Engines=null,bool Enabled=true);

internal static class ManagedMcpEndpoints
{
    internal static void Map(WebApplication app,WorkbenchStartup startup)
    {
        // This group is deliberately outside the LAN federation endpoint metadata.
        var routes=app.MapGroup("/api/managed-mcps");
        WorkbenchStore Store()=>new(startup.Root);
        routes.MapGet("",(string? project)=>{
            using var db=Store();
            string? path=project==null?null:db.RegisteredProject(project);
            var managed=db.ManagedMcps().Select(m=>m.Masked()).ToArray();
            return Results.Ok(new {managed, native=EngineNames.All.SelectMany(e=>NativeMcpInventory.Scan(e,path)).ToArray(),
                compatibility=EngineNames.All.ToDictionary(e=>e,e=>new[]{"stdio","http","sse"}.Where(t=>McpConfig.Supports(e,t)).ToArray())});
        });
        routes.MapPost("",(ManagedMcp input)=>{using var db=Store();return db.SaveMcps([input]).Single();});
        routes.MapPut("/{id}",(string id,ManagedMcp input)=>{using var db=Store();return db.SaveMcps([input],WorkbenchInput.Id(id)).Single();});
        routes.MapDelete("/{id}",(string id,string revision)=>{using var db=Store();db.DeleteMcp(WorkbenchInput.Id(id),revision);return Results.NoContent();});
        routes.MapPost("/import",(McpImport input)=>{
            if(input.McpServers.ValueKind!=JsonValueKind.Object) throw new WorkbenchError("请提供含 mcpServers 对象的 JSON");
            var configs=input.McpServers.EnumerateObject().Take(129).Select(p=>Parse(p,input)).ToArray();
            using var db=Store();return db.SaveMcps(configs);
        });
        routes.MapPost("/{id}/preflight",async(string id,string? project,CancellationToken ct)=>{
            using var db=Store();
            string key=WorkbenchInput.Id(id);
            var m=db.ManagedMcps().Find(x=>x.Id==key)??throw new WorkbenchError("MCP 已删除",WorkbenchFailure.NotFound);
            if(m.Transport=="stdio")
            {
                string? cwd=m.ProjectPath??(project==null?null:db.RegisteredProject(project));
                bool found=ExecutableExists(m.Command!,cwd);
                return Results.Ok(new {ok=found,message=found?"已找到可执行文件":"未找到可执行文件，请检查命令与 PATH"});
            }
            var uri=new Uri(m.Url!);
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var socket=new TcpClient();
            try {await socket.ConnectAsync(uri.Host,uri.Port,timeout.Token);return Results.Ok(new {ok=true,message="TCP 端口可连接"});}
            catch(Exception e) when(!ct.IsCancellationRequested && e is SocketException or OperationCanceledException)
            {return Results.Ok(new {ok=false,message="TCP 连接失败或超时"});}
        });
    }

    private static ManagedMcp Parse(JsonProperty server,McpImport options)
    {
        var obj=server.Value;
        if(obj.ValueKind!=JsonValueKind.Object) throw new WorkbenchError("每个 MCP 必须是配置对象");
        try
        {
            foreach(var p in obj.EnumerateObject())
                if(p.Name is not ("type" or "transport" or "url" or "httpUrl" or "command" or "args" or "env" or "headers" or "disabled"))
                    throw new WorkbenchError("导入包含不支持的 MCP 配置字段，请先核对");
            string? Str(string key)=>obj.TryGetProperty(key,out var v)?v.GetString():null;
            T? Get<T>(string key)=>obj.TryGetProperty(key,out var v)?v.Deserialize<T>():default;
            string? url=Str("httpUrl")??Str("url");
            string transport=(Str("type")??Str("transport")??(url==null?"stdio":"http")) switch {
                "local"=>"stdio","streamable-http" or "streamableHttp"=>"http",var t=>t };
            return new(){Name=server.Name,Command=Str("command"),Transport=transport,Url=url,Args=Get<string[]>("args")??[],
                Env=Get<Dictionary<string,string?>>("env")??[],Headers=Get<Dictionary<string,string?>>("headers")??[],
                Scope=options.Scope,ProjectPath=options.ProjectPath,Engines=options.Engines??[],Enabled=options.Enabled&&!Get<bool>("disabled")};
        }
        catch(Exception e) when(e is JsonException or InvalidOperationException)
        {throw new WorkbenchError("导入字段类型无效，请检查字符串、布尔值和键值对象");}
    }

    private static bool ExecutableExists(string command,string? cwd)
    {
        string[] extensions=OperatingSystem.IsWindows()?(Environment.GetEnvironmentVariable("PATHEXT")??".EXE;.CMD;.BAT;.COM").Split(';'):[""];
        bool Found(string path)
        {
            if(OperatingSystem.IsWindows())
                return Path.HasExtension(path)?extensions.Contains(Path.GetExtension(path),StringComparer.OrdinalIgnoreCase)&&File.Exists(path):
                    extensions.Any(ext=>File.Exists(path+ext));
            return File.Exists(path) && (File.GetUnixFileMode(path)&(UnixFileMode.UserExecute|UnixFileMode.GroupExecute|UnixFileMode.OtherExecute))!=0;
        }
        if(Path.IsPathFullyQualified(command)) return Found(command);
        if(command.Contains('/')||command.Contains('\\')) return cwd!=null&&Found(Path.GetFullPath(command,cwd));
        return (Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator,StringSplitOptions.RemoveEmptyEntries)
            .Where(Path.IsPathFullyQualified).Any(d=>Found(Path.Combine(d,command)));
    }
}

using FF.AgentCollaboration.Application;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Storage;
using FF.AgentCollaboration.Updates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FF.AgentCollaboration.Presentation.Http;

internal static class UpdateEndpoints
{
    internal static void Map(WebApplication app,WorkbenchStartup startup)
    {
        var updates=new ManagedUpdates("workbench",UpdateSupervisor.Version(typeof(Workbench).Assembly),app.Configuration,app.Lifetime);
        var local=app.MapGroup("/api/updates").AddEndpointFilter((context,next)=>
            WorkbenchHost.IsLoopback(context.HttpContext)?next(context):ValueTask.FromResult<object?>(Results.StatusCode(403)));

        List<RestartTask> Tasks()
        {
            if(!startup.Ready.IsCompletedSuccessfully)return [];
            using var db=new WorkbenchStore(startup.Root);
            return db.PendingRestartTasks();
        }

        IResult Schedule(UpdateRequest input,Workbench workbench,string? agent=null)
        {
            if(!startup.Ready.IsCompletedSuccessfully)return Results.Conflict(new {error="请先完成工作台初始化"});
            try
            {
                var selected=(input.Tasks??[]).Select(WorkbenchInput.Id).ToArray();
                var id=updates.Schedule(input with {Tasks=selected},workbench.IdleForUpdate,(operation,ids)=>
                {
                    using var db=new WorkbenchStore(startup.Root);
                    if(agent!=null && (!db.Windows().Any(window=>window.Id==agent && window.AllowPlatformUpdate) || db.DedicatedReader(agent)))
                        throw new UpdateFailure("当前 Agent 的更新权限已撤销");
                    try { db.BindRestartTasks(operation,ids); }
                    catch(Microsoft.Data.Sqlite.SqliteException) { throw new UpdateFailure("无法保存此次重启的任务，请重试。"); }
                });
                return Results.Accepted(value:new {id,status="scheduled"});
            }
            catch(UpdateFailure error){return Results.Conflict(new {error=error.Message});}
        }

        local.MapGet("",async (CancellationToken ct)=>new {release=await updates.Check(ct),tasks=Tasks()});
        local.MapGet("/progress",()=>new {release=updates.Status,tasks=Tasks()});
        local.MapPost("",(HttpRequest request,UpdateRequest input,Workbench workbench)=>
            WorkbenchHost.IsLocalPage(request.HttpContext)
                ? Schedule(input,workbench) : Results.Json(new {error="请从本机工作台安排更新或重启"},statusCode:403));
        local.MapPost("/cancel",(HttpRequest request)=>
            !WorkbenchHost.IsLocalPage(request.HttpContext)
                ? Results.Json(new {error="请从本机工作台取消安排"},statusCode:403)
                : updates.Cancel()?Results.NoContent():Results.Conflict(new {error="当前更新已经结束或正在重启"}));
        local.MapPost("/agent",async (HttpRequest request,AgentUpdate input,Workbench workbench,CancellationToken ct)=>
        {
            if(!startup.Ready.IsCompletedSuccessfully || request.Headers.Authorization.ToString() is not {Length:71} bearer ||
                !bearer.StartsWith("Bearer ",StringComparison.Ordinal) || !FederationPolicy.IsSecret(bearer[7..]))
                return Results.StatusCode(403);
            using var db=new WorkbenchStore(startup.Root);
            var caller=db.Caller(bearer[7..]);
            if(db.DedicatedReader(caller.Id))return Results.StatusCode(403);
            if(input.Action=="check")return Results.Ok(await updates.Check(ct));
            if(!caller.AllowPlatformUpdate)return Results.Json(new {error="请让用户在此 Agent 的设置中启用工作台更新权限"},statusCode:403);
            return Schedule(new(input.Action,input.Version,input.Mode??"idle",Tasks().Where(task=>task.Window==caller.Id && !task.Assigned)
                .Select(task=>task.Id).ToArray()),workbench,caller.Id);
        });
    }

    private sealed record AgentUpdate(string Action,string? Version,string? Mode);
}

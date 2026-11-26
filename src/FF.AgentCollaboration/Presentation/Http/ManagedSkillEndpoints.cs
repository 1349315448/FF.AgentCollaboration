using System.Text.Json;
using FF.AgentCollaboration.Application;
using FF.AgentCollaboration.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace FF.AgentCollaboration.Presentation.Http;

internal static class ManagedSkillEndpoints
{
    private sealed record NativeRemoval(string? ConfirmName,string? Revision);
    internal static void Map(WebApplication app,WorkbenchStartup startup)
    {
        var group=app.MapGroup("/api/managed-skills");
        ManagedSkills Manager()=>new(startup.Root);
        group.MapGet("",(string? project)=>Manager().Catalog(project));
        group.MapGet("/platform/ff-agent-collab",()=>new {content=CollaborationSkill.Read()});
        group.MapGet("/{id}",(string id)=>Manager().Detail(WorkbenchInput.Id(id)));
        group.MapPost("",async(HttpRequest req)=>Manager().Save(null,await Read(req.HttpContext,false)));
        group.MapPut("/{id}",async(string id,HttpContext ctx)=>Manager().Save(WorkbenchInput.Id(id),await Read(ctx,false)));
        group.MapPost("/import",async(HttpRequest req)=>Manager().Save(null,await Read(req.HttpContext,true),true));
        group.MapDelete("/{id}",(string id,string revision)=>Manager().Delete(WorkbenchInput.Id(id),revision));
        group.MapPost("/{id}/sync",(string id)=>Manager().Sync(WorkbenchInput.Id(id)));
        group.MapGet("/native/{id}",(string id,string? project)=>Manager().ReadNative(id,project));
        group.MapPost("/native-cleanup/{id}",(string id)=>{Manager().RetryNative(WorkbenchInput.Id(id));return Results.NoContent();});
        group.MapDelete("/native/{id}",(string id,string? project,[FromBody] NativeRemoval input)=>{
            Manager().DeleteNative(id,project,input.ConfirmName,input.Revision);
            return Results.NoContent();
        });
    }
    private static async Task<SkillEdit> Read(HttpContext ctx,bool import)
    {
        // 只扩大本机编辑/导入接口的 JSON 传输预算，包内还按实际解码字节校验。
        var limit=ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if(limit is {IsReadOnly:false}) limit.MaxRequestBodySize=import?12*1024*1024:4*1024*1024;
        try {return await ctx.Request.ReadFromJsonAsync<SkillEdit>(ctx.RequestAborted)??throw new WorkbenchError("缺少 Skill 配置");}
        catch(JsonException){throw new WorkbenchError("Skill 配置或文件编码无效");}
    }
}

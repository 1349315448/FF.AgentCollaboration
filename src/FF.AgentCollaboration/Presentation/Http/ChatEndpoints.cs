using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using FF.AgentCollaboration.Application;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Files;
using FF.AgentCollaboration.Infrastructure.Storage;

namespace FF.AgentCollaboration.Presentation.Http;

internal static class ChatEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static void Map(WebApplication app,WorkbenchStartup startup)
    {
        var chat = app.MapGroup("/api/topics/{id}/chat");
        chat.MapGet("", (string id,string? before,string? refresh) =>
        {
            using var db = new WorkbenchStore(startup.Root);
            return db.ChatHistory(WorkbenchInput.Id(id),before,refresh?.Split(','));
        });
        chat.MapGet("/links",(string id) =>
        {
            using var db = new WorkbenchStore(startup.Root);
            return db.ChatLinks(WorkbenchInput.Id(id));
        });
        chat.MapPost("/links",(string id,ChatLinkInput input) =>
        {
            using var db = new WorkbenchStore(startup.Root);
            db.SetChatLink(WorkbenchInput.Id(id),WorkbenchInput.Id(input.Group),true);
            return Results.NoContent();
        });
        chat.MapDelete("/links/{group}",(string id,string group) =>
        {
            using var db = new WorkbenchStore(startup.Root);
            db.SetChatLink(WorkbenchInput.Id(id),WorkbenchInput.Id(group),false);
            return Results.NoContent();
        });
        chat.MapPut("/links",(string id,ChatLinkSelection[] input) =>
        {
            using var db = new WorkbenchStore(startup.Root);
            db.ConfigureChatLinks(WorkbenchInput.Id(id),input);
            return Results.NoContent();
        });
        chat.MapPost("", async (string id,HttpRequest request,CancellationToken ct) =>
        {
            var bodySize = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (bodySize is { IsReadOnly:false }) bodySize.MaxRequestBodySize = WorkbenchStore.ChatUploadLimit + 1024 * 1024;
            string topic = WorkbenchInput.Id(id);
            ChatInput? input;
            var uploads = new List<ChatUpload>();
            try
            {
                if (request.HasFormContentType)
                {
                    var form = await request.ReadFormAsync(ct);
                    if (form["message"].ToString().Length > 760_000 || form.Files.Count > 20)
                        throw new WorkbenchError("附件或消息超过上限");
                    input = JsonSerializer.Deserialize<ChatInput>(form["message"].ToString(),Json);
                    foreach (var file in form.Files) uploads.Add(new(file.FileName,file.OpenReadStream()));
                }
                else input = await request.ReadFromJsonAsync<ChatInput>(ct);
                using var db = new WorkbenchStore(startup.Root);
                var post = db.PostUserChat(topic,input ?? throw new WorkbenchError("请填写消息"),uploads.ToArray());
                return Results.Ok(new { id = post });
            }
            catch (JsonException) { return Results.BadRequest(new { error = "聊天消息格式无效" }); }
            finally
            {
                foreach (var upload in uploads) upload.Content.Dispose();
            }
        });
        chat.MapGet("/{message}/attachments/{key}",(string id,string message,string key,string? view,HttpContext context) =>
        {
            using var db = new WorkbenchStore(startup.Root);
            string topic = WorkbenchInput.Id(id);
            var (path,name) = db.ChatFile(topic,message,key);
            if (view == "text") return Results.Ok(ProjectFileBrowser.ReadDocument(path));
            var file = ProjectFileBrowser.OpenVerified(path);
            if (view == "inline")
            {
                try
                {
                    var format = WorkspacePreviewFormat.Detect(file,path);
                    if (format == null || format.Value.Kind is not ("image" or "pdf"))
                        throw new WorkbenchError("此文件请下载后查看");
                    context.Response.Headers.XContentTypeOptions = "nosniff";
                    context.Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'self'";
                    return Results.File(file,format.Value.Mime,enableRangeProcessing:true);
                }
                catch { file.Dispose(); throw; }
            }
            return Results.File(file,"application/octet-stream",name,enableRangeProcessing:true);
        });
    }
}

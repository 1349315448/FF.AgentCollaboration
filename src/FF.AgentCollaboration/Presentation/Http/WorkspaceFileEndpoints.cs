using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using FF.AgentCollaboration.Application;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Files;
using FF.AgentCollaboration.Infrastructure.Storage;

namespace FF.AgentCollaboration.Presentation.Http;

internal static class WorkspaceFileEndpoints
{
    internal static void Map(WebApplication app, WorkbenchStartup startup)
    {
        ProjectFileBrowser Browser(string project)
        {
            using var store = new WorkbenchStore(startup.Root);
            return new(store.RegisteredProject(project));
        }
        var files = app.MapGroup("/api/workspace");
        files.MapGet("/tree", (string project, string? path, bool? showIgnored, CancellationToken ct) =>
            new { entries = Browser(project).Tree(path, showIgnored == true, ct) });
        files.MapGet("/search", (string project, string? q, bool? showIgnored, CancellationToken ct) =>
            Browser(project).Search(q, showIgnored == true, ct));
        files.MapPost("/open-folder", (HttpContext context, string project, string path) =>
        {
            if (!WorkbenchHost.IsLoopback(context)) return Results.StatusCode(403);
            Browser(project).OpenFolder(path);
            return Results.NoContent();
        });
        files.MapGet("/file", (HttpContext context, string project, string path, bool? image, bool? raw) =>
        {
            var browser = Browser(project);
            if (image != true && raw != true) return Results.Ok(browser.Read(path));
            var media = browser.OpenPreview(path, image == true);
            // 原始 SVG 在直接导航时仍须隔离脚本。只有 PDF 查看器允许被本机工作台嵌入。
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.ContentSecurityPolicy = media.Mime == "application/pdf"
                ? "frame-ancestors 'self'; base-uri 'none'" : "sandbox; default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'";
            return Results.File(media.Stream, media.Mime, enableRangeProcessing: true);
        });
        files.MapPut("/file", (WorkspaceEdit edit) => Browser(edit.Project).Save(edit))
            .WithMetadata(new RequestSizeLimitAttribute(6 * ProjectFileBrowser.Limit + 64 * 1024));
        files.MapGet("/documents", (string topic) =>
        {
            using var store = new WorkbenchStore(startup.Root);
            return store.WorkspaceDocuments(WorkbenchInput.Id(topic));
        });
        files.MapGet("/documents/{id}", (string topic, string id) =>
        {
            using var store = new WorkbenchStore(startup.Root);
            return ProjectFileBrowser.ReadDocument(store.WorkspaceDocument(WorkbenchInput.Id(topic), WorkbenchInput.Id(id)));
        });
    }
}

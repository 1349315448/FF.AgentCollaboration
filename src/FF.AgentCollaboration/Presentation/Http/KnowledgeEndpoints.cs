using FF.AgentCollaboration.Application;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace FF.AgentCollaboration.Presentation.Http;

internal static class KnowledgeEndpoints
{
    private sealed record ProjectMode(string Project, string Mode);
    private sealed record DocumentEdit(string Project, string Path, string Content, string? Version);

    internal static void Map(WebApplication app, WorkbenchStartup startup)
    {
        var group = app.MapGroup("/api/knowledge");
        group.AddEndpointFilter((context, next) => WorkbenchHost.IsLoopback(context.HttpContext)
            ? next(context) : new ValueTask<object?>(Results.StatusCode(StatusCodes.Status403Forbidden)));
        group.MapPut("/project", (ProjectMode input) =>
        {
            using var state = new WorkbenchStore(startup.Root);
            return new { mode = WorkbenchKnowledge.Configure(state, input.Project, input.Mode) };
        });
        group.MapGet("/search", (string project, string? query, string? scope, string? theme, string[] keywords,
            string? topicId, string? agentId, int? limit, bool? recordsOnly, int? offset, CancellationToken ct) =>
        {
            using var state = new WorkbenchStore(startup.Root);
            return WorkbenchKnowledge.Search(state, project, new KnowledgeQuery(query, scope, theme, keywords, topicId, limit, agentId,
                recordsOnly ?? false, offset ?? 0), ct: ct);
        });
        group.MapGet("/document", (string project, string path) =>
        {
            using var state = new WorkbenchStore(startup.Root);
            return WorkbenchKnowledge.Read(state, project, path);
        });
        group.MapPut("/document", (DocumentEdit input) =>
        {
            using var state = new WorkbenchStore(startup.Root);
            string path = WorkbenchInput.Text(input.Path, "知识库路径", 512);
            string scope = path.Replace('\\', '/').Split('/')[0] switch
            {
                "project" => "project",
                "topics" => "topic",
                "agents" => "agent",
                _ => throw new WorkbenchError("请选择一个知识库文档")
            };
            return WorkbenchKnowledge.Write(state, input.Project,
                new KnowledgeWrite(scope, path, input.Content, ExpectedVersion: input.Version));
        }).WithMetadata(new RequestSizeLimitAttribute(6 * 512 * 1024 + 64 * 1024));
    }
}

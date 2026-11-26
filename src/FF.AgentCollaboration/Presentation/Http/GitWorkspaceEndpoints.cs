using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using FF.AgentCollaboration.Application;
using FF.AgentCollaboration.Infrastructure.Files;
using FF.AgentCollaboration.Infrastructure.Storage;

namespace FF.AgentCollaboration.Presentation.Http;

internal static class GitWorkspaceEndpoints
{
    internal static void Map(WebApplication app, WorkbenchStartup startup)
    {
        GitWorkspaceService Service(string project)
        {
            using var store = new WorkbenchStore(startup.Root);
            return new(store.RegisteredProject(project));
        }

        var git = app.MapGroup("/api/git");
        git.MapGet("/status", (string project, CancellationToken ct) => Service(project).Status(ct));
        git.MapGet("/diff", (string project, string path, bool? staged, CancellationToken ct) =>
            Service(project).Diff(path, staged == true, ct));
        git.MapGet("/log", (string project, int? limit, CancellationToken ct) => Service(project).Log(limit ?? 15, ct));
        foreach (string action in new[] { "stage", "unstage", "discard", "commit", "pull", "push" })
            git.MapPost("/" + action, async (GitAction input, CancellationToken ct) =>
            {
                await Service(input.Project).Apply(action, input, ct);
                return Results.NoContent();
            });
    }
}

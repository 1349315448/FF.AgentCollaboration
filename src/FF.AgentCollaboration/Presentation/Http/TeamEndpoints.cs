using FF.AgentCollaboration.Application;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FF.AgentCollaboration.Presentation.Http;

internal static class TeamEndpoints
{
    internal static void Map(WebApplication app, WorkbenchStartup startup)
    {
        var decisions = app.MapGroup("/api/topics/{topic}/team-proposals/{proposal}");
        decisions.AddEndpointFilter((context, next) => WorkbenchHost.IsLoopback(context.HttpContext)
            ? next(context) : ValueTask.FromResult<object?>(Results.StatusCode(StatusCodes.Status403Forbidden)));
        decisions.MapPost("/confirm", (string topic, string proposal, TeamConfirmation input, Workbench workbench, CancellationToken ct) =>
            workbench.ConfirmTeamProposal(WorkbenchInput.Id(topic), WorkbenchInput.Id(proposal), input, ct));
        decisions.MapPost("/reject", (string topic, string proposal, Workbench workbench) =>
        {
            workbench.RejectTeamProposal(WorkbenchInput.Id(topic), WorkbenchInput.Id(proposal));
            return Results.NoContent();
        });
        app.MapPost("/api/team/engine-catalog", (HttpContext context, SessionCatalog catalog) =>
        {
            if (!WorkbenchHost.IsLoopback(context)) throw new WorkbenchError("仅本机 Agent 可以读取引擎目录", WorkbenchFailure.Forbidden);
            using var state = new WorkbenchStore(startup.Root);
            var caller = state.Caller(context.Request.Headers["X-FF-Window-Grant"].ToString());
            if (state.DedicatedReader(caller.Id)) throw new WorkbenchError("专用审阅终端无权读取本机引擎目录", WorkbenchFailure.Forbidden);
            return catalog.AvailableTeamEngines(caller.Project);
        });
    }
}

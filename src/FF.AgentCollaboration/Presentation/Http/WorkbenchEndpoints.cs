using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using FF.AgentCollaboration.Application;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Engines;
using FF.AgentCollaboration.Infrastructure.Files;
using FF.AgentCollaboration.Infrastructure.Storage;

namespace FF.AgentCollaboration.Presentation.Http;

internal sealed record LogCleanupRequest(int? Days);

internal static class WorkbenchEndpoints
{
    internal static void Map(WebApplication app, WorkbenchStartup startup, WorkbenchLocation location)
    {
        app.MapGet("/api/initialize", startup.Status);
        app.MapPost("/api/initialize", (WorkbenchPaths paths) =>
        {
            startup.Initialize(paths);
            return Results.NoContent();
        });
        app.MapGet("/api/state", (string? topic, int? recent, Workbench workbench) =>
            workbench.Snapshot(topic == null ? null : WorkbenchInput.Id(topic), Math.Clamp(recent ?? 6, 1, 30)));
        app.MapGet("/api/messages/{id}", (string id) =>
        {
            using var state = new WorkbenchStore(startup.Root);
            return state.Preview(WorkbenchInput.Id(id));
        });
        app.MapPost("/api/windows/{id}/inbox/{messageId}/acknowledge", (string id, string messageId) =>
        {
            using var state = new WorkbenchStore(startup.Root);
            state.Acknowledge(WorkbenchInput.Id(id), WorkbenchInput.Id(messageId));
            return Results.NoContent();
        });
        app.MapPost("/api/windows/{id}/inbox/acknowledge-all", (string id) =>
        {
            using var state = new WorkbenchStore(startup.Root);
            state.AcknowledgeAll(WorkbenchInput.Id(id));
            return Results.NoContent();
        });
        app.MapGet("/api/topics", () => { using var state = new WorkbenchStore(startup.Root); return state.Topics(); });
        app.MapGet("/api/topic-folders", () => { using var state = new WorkbenchStore(startup.Root); return state.ArchiveChoices(); });
        app.MapPost("/api/topics", (TopicInput input) => { using var state = new WorkbenchStore(startup.Root); return state.SaveTopic(null, input); });
        app.MapPut("/api/topics/{id}", (string id, TopicInput input) =>
        {
            using var state = new WorkbenchStore(startup.Root);
            return state.SaveTopic(WorkbenchInput.Id(id), input);
        });
        app.MapPost("/api/topics/{id}/finish", (string id) => { using var state = new WorkbenchStore(startup.Root); return state.SetFinished(WorkbenchInput.Id(id), true); });
        app.MapPost("/api/topics/{id}/reopen", (string id) => { using var state = new WorkbenchStore(startup.Root); return state.SetFinished(WorkbenchInput.Id(id), false); });
        app.MapGet("/api/shortcuts/{engine}", (string engine) =>
        {
            using var state = new WorkbenchStore(startup.Root);
            return state.Shortcuts(engine);
        });
        app.MapPost("/api/shortcuts/{engine}", (string engine, ChangeShortcut request) =>
        {
            using var state = new WorkbenchStore(startup.Root);
            return state.ChangeShortcuts(engine, request);
        });
        app.MapGet("/api/folders", (string? path) => ProjectFolders.List(path));
        app.MapPut("/api/project", (SelectProject request) =>
        {
            var selected = ProjectFolders.Resolve(request.Path);
            using var state = new WorkbenchStore(startup.Root);
            state.SelectProject(selected);
            return new { project = selected };
        });
        app.MapDelete("/api/project", (string path) =>
        {
            using var state = new WorkbenchStore(startup.Root);
            state.RemoveProject(ProjectFolders.Resolve(path));
            return Results.NoContent();
        });
        app.MapGet("/api/settings/logs", () =>
        {
            using var store = new WorkbenchStore(startup.Root);
            return new { total = store.LogCount() };
        });
        app.MapPost("/api/settings/logs/cleanup", (LogCleanupRequest input) =>
        {
            using var store = new WorkbenchStore(startup.Root);
            return store.CleanLogs(input.Days);
        });
        app.MapPut("/api/settings/mention-shortcut", (MentionKey input) =>
        {
            using var store = new WorkbenchStore(startup.Root);
            return new { chord = store.SaveMentionShortcut(input.Chord) };
        });
        app.MapGet("/api/settings", () => { using var state = new WorkbenchStore(startup.Root); return location.Settings(state); });
        app.MapPut("/api/settings", (WorkbenchPaths input) =>
        {
            using var state = new WorkbenchStore(startup.Root);
            return location.Save(state, input);
        });
        app.MapGet("/api/environment", async (CancellationToken ct) =>
        {
            using var state = new WorkbenchStore(startup.Root);
            return await WorkbenchEnvironment.Inspect(state, location.Target, ct);
        });
        app.MapPost("/api/environment/repair", async (RepairRequest input) =>
        {
            using var state = new WorkbenchStore(startup.Root);
            if (input.Item == "data_dir") location.RefreshPendingBackup();
            await WorkbenchEnvironment.Repair(state, location.Target, input.Item);
            return Results.NoContent();
        });
        app.MapGet("/api/engines/capabilities", () => WorkbenchEnvironment.Engines.ToDictionary(name => name, name =>
        {
            using var adapter = Engine.Create(name, Directory.GetCurrentDirectory());
            return adapter.Capabilities;
        }));
        app.MapGet("/api/engines", async (bool? detail, CancellationToken ct) =>
        {
            if (detail == true) return Results.Json(await Task.WhenAll(WorkbenchEnvironment.Engines.Select(e => WorkbenchEnvironment.EngineDetails(e, ct))));
            return Results.Json(WorkbenchEnvironment.Engines.ToDictionary(name => name, name => EngineExecutable.TryFindExecutable(name) != null));
        });
        app.MapPost("/api/engines/{engine}/install", async (string engine, CancellationToken cancellation) =>
        {
            await EngineInstallation.Install(EngineNames.Require(engine), cancellation);
            return Results.NoContent();
        });
        app.MapGet("/api/models/{engine}", (string engine, string? project, SessionCatalog catalog, CancellationToken cancellation) =>
            catalog.Models(engine, project, cancellation));
        app.MapGet("/api/quota/{engine}", (string engine, string? refresh, SessionCatalog catalog, CancellationToken cancellation) =>
            catalog.Quota(engine, refresh == "1", cancellation));
        app.MapGet("/api/sessions/{engine}", (string engine, string? project, bool? archived, bool? includeBound,
            SessionCatalog catalog, CancellationToken cancellation) =>
            catalog.Sessions(engine, project, cancellation, archived == true, includeBound == true));
        app.MapPost("/api/windows/{id}/history/{action}", async (string id, string action, HistoryAction input,
            Workbench workbench, CancellationToken cancellation) =>
        {
            await workbench.ChangeHistory(WorkbenchInput.Id(id), action, input, cancellation);
            return Results.NoContent();
        });
        app.MapPost("/api/windows/{id}/mention", async (string id, MentionTarget input, Workbench workbench, CancellationToken ct) =>
        {
            await workbench.InsertMention(WorkbenchInput.Id(id), input, ct);
            return Results.NoContent();
        });
        app.MapGet("/api/files", (string project, string? q, CancellationToken cancellation) =>
            ProjectFolders.Files(ProjectFolders.Resolve(project), q, cancellation));
        // 独立分支入口可让新版前端在旧宿主上明确失败，避免旧宿主忽略新字段后接管原会话。
        app.MapPost("/api/windows/fork", (CreateAgent request, Workbench workbench, CancellationToken cancellation) =>
            workbench.Create(request with { ForkSession = true }, cancellation));
        app.MapPost("/api/windows", (CreateAgent request, Workbench workbench, CancellationToken cancellation) =>
            workbench.Create(request, cancellation));
        app.Map("/api/windows/{id}/terminal", (string id, HttpContext context, Workbench workbench) =>
            TerminalSocket.Run(context, workbench.Terminal(WorkbenchInput.Id(id)), app.Lifetime.ApplicationStopping));
        app.MapPost("/api/windows/{id}/input", async (string id, TerminalInput request, Workbench workbench, CancellationToken cancellation) =>
        {
            await workbench.WriteInput(WorkbenchInput.Id(id), request, cancellation);
            return Results.NoContent();
        });
        app.MapPost("/api/windows/{id}/start", async (string id, Workbench workbench, CancellationToken cancellation) =>
        {
            await workbench.Start(WorkbenchInput.Id(id), cancellation);
            return Results.NoContent();
        });
        app.MapPost("/api/windows/{id}/session", async (string id, ChangeSession request, Workbench workbench, CancellationToken cancellation) =>
        {
            await workbench.SwitchSession(WorkbenchInput.Id(id), request, cancellation);
            return Results.NoContent();
        });
        app.MapPut("/api/windows/{id}/name", (string id, RenameAgent request) =>
        {
            using var state = new WorkbenchStore(startup.Root);
            state.RenameWindow(WorkbenchInput.Id(id), WorkbenchInput.AgentName(request.Name));
            return Results.NoContent();
        });
        app.MapPut("/api/windows/{id}/auto-receive", (string id, ChangeAutoReceive request, Workbench workbench) =>
        {
            workbench.Toggle(WorkbenchInput.Id(id), request.Enabled);
            return Results.NoContent();
        });
        app.MapPost("/api/windows/{id}/stop", async (string id, Workbench workbench, CancellationToken cancellation) =>
        {
            await workbench.Stop(WorkbenchInput.Id(id), cancellation);
            return Results.NoContent();
        });
        app.MapDelete("/api/windows/{id}", async (string id, Workbench workbench, CancellationToken cancellation) =>
        {
            await workbench.Stop(WorkbenchInput.Id(id), cancellation, removeWindow: true);
            return Results.NoContent();
        });
    }
}

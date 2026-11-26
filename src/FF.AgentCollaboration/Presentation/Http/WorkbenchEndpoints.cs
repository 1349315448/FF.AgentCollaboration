using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using FF.AgentCollaboration.Application;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Engines;
using FF.AgentCollaboration.Infrastructure.Files;
using FF.AgentCollaboration.Infrastructure.Storage;
using FF.AgentCollaboration.Infrastructure.Terminal;
using FF.AgentCollaboration.Infrastructure.Windows;

namespace FF.AgentCollaboration.Presentation.Http;

internal sealed record LogCleanupRequest(int? Days);

internal static class WorkbenchEndpoints
{
    internal static void Map(WebApplication app, WorkbenchStartup startup, WorkbenchLocation location)
    {
        WorkspaceFileEndpoints.Map(app, startup);
        GitWorkspaceEndpoints.Map(app, startup);
        ManagedMcpEndpoints.Map(app, startup);
        ManagedSkillEndpoints.Map(app, startup);
        app.MapGet("/api/initialize", startup.Status);
        app.MapPost("/api/initialize", (InitializeWorkbench paths) =>
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
        app.MapPost("/api/messages/{id}/resend", (string id, WorkbenchEvents events) =>
        {
            using var state = new WorkbenchStore(startup.Root);
            var item = state.ResendLocal(WorkbenchInput.Id(id));
            events.Changed();
            return Results.Ok(new { item.Id });
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
            return adapter.Capabilities with { StartupApproval = StartupApproval.For(name) };
        }));
        app.MapGet("/api/engines/{engine}/account", (string engine, string project, CancellationToken ct) =>
            EngineAuthentication.Check(EngineNames.Require(engine), ProjectFolders.Resolve(project), ct));
        // 登录终端只属于此 WebSocket；离开弹窗即释放，整个过程不创建窗口、会话或 MCP 授权。
        app.MapGet("/api/engines/{engine}/login", async (string engine, string project, string? provider, HttpContext context) =>
        {
            if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
            string name = EngineNames.Require(engine), folder = ProjectFolders.Resolve(project);
            using var adapter = Engine.Create(name, folder);
            if (provider != null && (name != "zcode" || provider is not ("bigmodel" or "zai"))) throw new WorkbenchError("未知的登录方式");
            IReadOnlyList<string> args = provider == null ? adapter.LoginArguments : ["login", provider];
            using var terminal = new TerminalSession(name, EngineExecutable.FindExecutable(name), args, folder,
                "", 90, 22, () => { }, adapter.LoginEnvironment);
            await TerminalSocket.RunLogin(context, terminal, app.Lifetime.ApplicationStopping);
        });
        app.MapGet("/api/engines", async (bool? detail, CancellationToken ct) =>
        {
            if (detail == true) return Results.Json(await Task.WhenAll(WorkbenchEnvironment.Engines.Select(e => WorkbenchEnvironment.EngineDetails(e, ct))));
            return Results.Json(WorkbenchEnvironment.Engines.ToDictionary(name => name, name => EngineExecutable.TryFindExecutable(name) != null));
        });
        app.MapGet("/api/engines/{engine}/installed", async (string engine, CancellationToken ct) =>
        {
            string? path = EngineExecutable.TryFindExecutable(EngineNames.Require(engine));
            return new { ready = path != null && !string.IsNullOrWhiteSpace(await WindowsCommand.Version(path, ct)) };
        });
        app.MapPost("/api/engines/{engine}/install", async (string engine, CancellationToken cancellation) =>
        {
            await EngineInstallation.Install(EngineNames.Require(engine), cancellation);
            return Results.NoContent();
        });
        app.MapGet("/api/engines/{engine}/release", async (string engine, bool? refresh, CancellationToken ct) =>
        {
            string name = EngineNames.Require(engine);
            var latest = EngineReleases.Latest(name, refresh == true, ct);
            var installed = await WorkbenchEnvironment.EngineDetails(name, ct);
            string? latestVersion = await latest;
            return new { installed, latestVersion, status = EngineReleases.Compare(installed.Version, latestVersion),
                canUpdate = installed.Path != null && latestVersion != null && EngineInstallation.CanUpdate(name, installed.Path, installed.Version, latestVersion) };
        });
        app.MapPost("/api/engines/{engine}/update", async (string engine, Workbench workbench, CancellationToken ct) =>
        {
            await workbench.UpdateEngine(EngineNames.Require(engine), ct);
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
        app.MapPut("/api/windows/{id}/config", async (string id, AgentConfiguration request, Workbench workbench, CancellationToken ct) =>
        {
            await workbench.ConfigureAgent(WorkbenchInput.Id(id), request, ct);
            return Results.NoContent();
        });
        app.MapPut("/api/windows/{id}/name", (string id, RenameAgent request) =>
        {
            using var state = new WorkbenchStore(startup.Root);
            state.RenameWindow(WorkbenchInput.Id(id), WorkbenchInput.AgentName(request.Name));
            return Results.NoContent();
        });
        app.MapPut("/api/windows/{id}/auto-receive", async (string id, ChangeAutoReceive request, Workbench workbench, CancellationToken cancellation) =>
        {
            await workbench.Toggle(WorkbenchInput.Id(id), request, cancellation);
            return Results.NoContent();
        });
        app.MapPost("/api/windows/{id}/approval", async (string id, OpenApproval request, Workbench workbench, CancellationToken cancellation) =>
        {
            await workbench.OpenApprovalMenu(WorkbenchInput.Id(id), request.Terminal, cancellation);
            return Results.NoContent();
        });
        app.MapPut("/api/windows/{id}/approval", async (string id, ChangeApproval request, Workbench workbench, CancellationToken cancellation) =>
        {
            await workbench.ChangeApprovalMode(WorkbenchInput.Id(id), request, cancellation);
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

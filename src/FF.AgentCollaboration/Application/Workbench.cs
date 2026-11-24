using System.Collections.Concurrent;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Engines;
using FF.AgentCollaboration.Infrastructure.Files;
using FF.AgentCollaboration.Infrastructure.Storage;
using FF.AgentCollaboration.Infrastructure.Terminal;

namespace FF.AgentCollaboration.Application;

// [01、04] 工作台持有原生终端实例。SQLite 中的消息在写入管道之前认领。
internal sealed class Workbench(WorkbenchStartup startup, SessionCatalog catalog, ILogger<Workbench> logger) : BackgroundService
{
    private string Root => startup.Root;
    private readonly ConcurrentDictionary<string, TerminalSession> terminals = new();
    private readonly SemaphoreSlim lifecycle = new(1);
    private int disposed;

    internal WorkbenchView Snapshot(string? topicId, int recent = 6)
    {
        using var state = new WorkbenchStore(Root);
        var windows = state.Windows().Select(agent =>
        {
            terminals.TryGetValue(agent.Id, out var terminal);
            var (online, inputReady, activity) = terminal?.ViewState() ?? (false, false, "offline");
            return new WindowView(agent with { Online = online }, terminal?.Generation, terminal?.ExitCode,
                inputReady, activity, state.Inbox(agent.Id));
        }).ToArray();
        var byTopic = windows.ToLookup(w => w.Agent.TopicId);
        var topics = state.Topics().Select(topic =>
        {
            var members = byTopic[topic.Id];
            string activity = topic.Status == "finished" ? "finished" :
                members.Any(w => w.Activity == "working") ? "working" :
                members.Any(w => w.Activity == "waiting" || w.Inbox.Any(i => i.State is "pending" or "delivery_uncertain")) ? "waiting" : "idle";
            return topic with { Activity = activity };
        }).ToArray();
        string current = state.Project();
        var projects = state.Projects(topics.Select(t => t.Project).Prepend(current).Concat(windows.Select(w => w.Agent.Project)));
        return new(current, projects, topics, windows, topicId, topicId == null ? [] : state.Timeline(topicId, recent));
    }

    internal TerminalSession Terminal(string id) => terminals.TryGetValue(id, out var terminal)
        ? terminal : throw new WorkbenchError("终端未启动", WorkbenchFailure.NotFound);

    internal async Task ChangeHistory(string windowId, string action, HistoryAction input, CancellationToken ct)
    {
        if (action is not ("archive" or "restore" or "delete")) throw new WorkbenchError("未知会话操作");
        string session = WorkbenchInput.Text(input.Session, "会话 ID", 256);
        await lifecycle.WaitAsync(ct);
        try
        {
            using var state = new WorkbenchStore(Root);
            var window = state.Window(windowId);
            var history = await catalog.Sessions(window.Engine, window.Project, ct, input.Archived, includeBound: true);
            var item = history.FirstOrDefault(s => s.Id == session) ?? throw new WorkbenchError("该会话已移除，或不在当前列表中", WorkbenchFailure.Conflict);
            if (item.MayBeInUse) throw new WorkbenchError("会话已绑定或可能仍在运行，请先切换或关闭对应终端", WorkbenchFailure.Conflict);
            if ((action == "archive" && input.Archived) || (action == "restore" && !input.Archived))
                throw new WorkbenchError("会话归档状态已变化，请刷新列表", WorkbenchFailure.Conflict);
            if (window.Engine == "codex" || action == "delete")
            {
                using var engine = Engine.Create(window.Engine, window.Project);
                using var deadline = SessionCatalog.Deadline(ct);
                await engine.ChangeHistory(item.Id, action, deadline.Token);
            }
            // Copilot 无原生归档 API；仅归档标记留在工作台，删除始终由原生 CLI 执行。
            if (window.Engine != "codex") state.SetArchive(window.Engine, item.Id, action == "archive");
        }
        finally { lifecycle.Release(); }
    }

    private async Task<string?> LatestCopilotSession(WorkbenchStore state, string id, string project, CancellationToken request)
    {
        // 原生新会话尚未绑定历史 ID 时，避免自动接管同项目的另一个在线窗格。
        if (state.Windows().Any(window => window.Id != id && window.Engine == "copilot" && window.Session == null &&
            ProjectFolders.Same(window.Project, project) && terminals.TryGetValue(window.Id, out var terminal) && terminal.Online))
            return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(request);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        try { return (await catalog.Sessions("copilot", project, deadline.Token)).FirstOrDefault()?.Id; }
        catch (Exception error) when (!request.IsCancellationRequested)
        {
            logger.LogDebug("Copilot history lookup unavailable: {Type}", error.GetType().Name);
            return null;
        }
    }

    internal async Task<AgentWindow> Create(CreateAgent request, CancellationToken ct)
    {
        string name = WorkbenchInput.AgentName(request.Name);
        string engineName = EngineNames.Require(request.Engine);
        using var state = new WorkbenchStore(Root);
        var topic = state.Topic(WorkbenchInput.Id(request.Topic));
        string project = ProjectFolders.Resolve(request.Project ?? topic.Project);
        string? session = EmptyToNull(request.Session);
        if (request.NewSession && session != null) throw new WorkbenchError("请选择新建或一个历史会话");
        if (request.ForkSession && session == null) throw new WorkbenchError("请选择用于创建分支的历史会话");
        string? model = Option(request.Model, "模型");
        string? effort = Option(request.Effort, "推理档位");
        string? mode = EmptyToNull(request.Mode);

        var window = new AgentWindow(Guid.NewGuid().ToString("N"), name, engineName, project, model, request.ForkSession ? null : session, effort, mode,
            AutoReceive: true, Online: false, TopicId: topic.Id);
        await lifecycle.WaitAsync(ct);
        try
        {
            if (session != null && !(await catalog.Sessions(engineName, project, ct, includeBound: request.ForkSession)).Any(item => item.Id == session))
                throw new WorkbenchError("这个历史会话不属于所选项目，或已在工作台打开", WorkbenchFailure.Conflict);
            if (engineName == "copilot" && session == null && !request.NewSession)
                window = window with { Session = await LatestCopilotSession(state, window.Id, project, ct) };
            state.AddWindow(window);
            try { return await Open(state, window, ct, request.ForkSession ? session : null); }
            catch
            {
                state.RemoveUnstartedWindow(window.Id);
                throw;
            }
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 19)
        {
            throw new WorkbenchError("这个 Agent 名称或原生会话已经使用", WorkbenchFailure.Conflict);
        }
        finally { lifecycle.Release(); }
    }

    internal async Task Start(string id, CancellationToken ct)
    {
        await lifecycle.WaitAsync(ct);
        try
        {
            using var state = new WorkbenchStore(Root);
            var window = state.Window(id);
            if (terminals.TryGetValue(id, out var existing) && existing.Online) return;
            if (window.Engine == "copilot" && window.Session == null)
                window = window with { Session = await LatestCopilotSession(state, id, window.Project, ct) };
            await Open(state, window, ct);
        }
        finally { lifecycle.Release(); }
    }

    internal async Task SwitchSession(string id, ChangeSession request, CancellationToken ct)
    {
        string? session = EmptyToNull(request.Session);
        if (request.NewSession == (session != null)) throw new WorkbenchError("请选择新建或一个历史会话");
        using var state = new WorkbenchStore(Root);
        await lifecycle.WaitAsync(ct);
        try
        {
            var window = state.Window(id);
            if (session != null && session != window.Session &&
                !(await catalog.Sessions(window.Engine, window.Project, ct)).Any(item => item.Id == session))
                throw new WorkbenchError("这个历史会话不属于所选项目，或已在工作台打开", WorkbenchFailure.Conflict);
            if (session != null && state.Windows().Any(other => other.Id != id && other.Engine == window.Engine && other.Session == session))
                throw new WorkbenchError("这个历史会话已在工作台打开", WorkbenchFailure.Conflict);
            if (session != null && session == window.Session && terminals.TryGetValue(id, out var current) && current.Online)
                return;

            await Open(state, window with { Session = session }, ct);
        }
        finally { lifecycle.Release(); }
    }

    private async Task<AgentWindow> Open(WorkbenchStore state, AgentWindow window, CancellationToken ct, string? forkSource = null)
    {
        using var engine = Engine.Create(window.Engine, window.Project);
        if (window.Mode != null && !engine.SupportsMode(window.Mode))
            throw new WorkbenchError("这个引擎不支持所选的初始模式");
        string? newTitle = null;
        if (window.Session == null)
        {
            newTitle = state.Topic(window.TopicId).Name + "-" + window.Name;
            using var timeout = SessionCatalog.Deadline(ct);
            string id = await engine.NewSession(newTitle, window.Model, forkSource, timeout.Token);
            if (id == forkSource || !Guid.TryParse(id, out _)) throw new WorkbenchError("引擎未返回独立的会话编号", WorkbenchFailure.Unavailable);
            window = window with { Session = id };
        }
        var grant = WorkbenchStore.NewGrant();
        var mcp = new EngineMcp(Root, "workbench-mcp", grant, CollaborationTools.Names);
        var arguments = engine.NativeArguments(window, mcp, newTitle, forkSource);
        if (terminals.TryGetValue(window.Id, out var previous))
        {
            state.Offline(window.Id);
            await Task.Run(previous.Dispose);
        }
        string? previousSession = state.Window(window.Id).Session;
        state.Start(window.Id, grant, window.Session);
        try
        {
            terminals[window.Id] = new TerminalSession(EngineExecutable.FindExecutable(window.Engine), arguments,
                ProjectFolders.Resolve(window.Project), grant, 100, 30);
            return window with { Online = true };
        }
        catch
        {
            state.RestoreSession(window.Id, previousSession);
            throw;
        }
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? Option(string? option, string label)
    {
        string? value = EmptyToNull(option);
        if (value != null && (value.Length > 140 || value.StartsWith('-') ||
            value.Any(character => !(char.IsAsciiLetterOrDigit(character) || "._:/-".Contains(character)))))
            throw new WorkbenchError($"{label}格式不正确");
        return value;
    }

    internal async Task Close(string id, CancellationToken ct)
    {
        await lifecycle.WaitAsync(ct);
        try
        {
            using var state = new WorkbenchStore(Root);
            state.Window(id);
            state.Offline(id);
            if (terminals.TryGetValue(id, out var terminal)) await Task.Run(terminal.Dispose);
            state.Close(id);
            terminals.TryRemove(id, out _);
        }
        finally { lifecycle.Release(); }
    }

    internal void Toggle(string id, bool enabled)
    {
        using var state = new WorkbenchStore(Root);
        state.Window(id);
        state.AutoReceive(id, enabled);
    }

    internal async Task WriteInput(string id, TerminalInput request, CancellationToken ct)
    {
        var terminal = Terminal(id);
        if (request.MessageId != null)
        {
            using var state = new WorkbenchStore(Root);
            var message = state.Pending(id, WorkbenchInput.Id(request.MessageId));
            if (!await Deliver(terminal, message, automatic: false, ct))
                throw new WorkbenchError("终端已退出或这条消息已处理", WorkbenchFailure.Conflict);
            return;
        }
        if (string.IsNullOrEmpty(request.Text) || request.Text.Length > 8192 || request.Text.Any(char.IsControl))
            throw new WorkbenchError("只能插入单行文本");
        await terminal.Input(Encoding.UTF8.GetBytes(request.Text), ct);
    }

    private async Task<bool> Deliver(TerminalSession terminal, InboxItem message, bool automatic, CancellationToken ct)
    {
        using var state = new WorkbenchStore(Root);
        bool claimed = false;
        try
        {
            bool written = await terminal.Deliver(message, automatic,
                () => claimed = state.ClaimMessage(message.Id, automatic),
                () =>
                {
                    state.FinishMessage(message.Id, "pending");
                    claimed = false;
                }, ct);
            if (written) state.FinishMessage(message.Id, "delivered");
            return written;
        }
        catch
        {
            if (claimed) state.FinishMessage(message.Id, "delivery_uncertain");
            throw;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await startup.Ready.WaitAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(400));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                using var state = new WorkbenchStore(Root);
                foreach (var window in state.Windows())
                    if (window.Online && terminals.TryGetValue(window.Id, out var terminal) && !terminal.Online)
                        await ReleaseExited(state, window.Id, terminal, stoppingToken);
                foreach (var entry in state.Automatic())
                {
                    if (!terminals.TryGetValue(entry.Target, out var terminal) || !terminal.CanDeliver(automatic: true)) continue;
                    try { await Deliver(terminal, entry, automatic: true, stoppingToken); }
                    catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException)
                    {
                        logger.LogWarning("Terminal write outcome for {Message} could not be confirmed", entry.Id);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task ReleaseExited(WorkbenchStore state, string id, TerminalSession expected, CancellationToken ct)
    {
        await lifecycle.WaitAsync(ct);
        try
        {
            if (terminals.TryGetValue(id, out var terminal) && ReferenceEquals(terminal, expected) && !terminal.Online)
            {
                state.Offline(id);
                await Task.Run(() => ReleaseInBackground(id, terminal));
            }
        }
        finally { lifecycle.Release(); }
    }

    // 后台清理一个已离线的窗格失败时，其余原生会话仍应继续工作。手动关闭或启动可重试。
    private void ReleaseInBackground(string id, TerminalSession terminal)
    {
        try { terminal.Dispose(); }
        catch (Exception error) when (error is WorkbenchError or System.ComponentModel.Win32Exception or IOException)
        {
            logger.LogWarning(error, "Could not finish closing terminal {WindowId}", id);
        }
    }

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        base.Dispose();
        foreach (var (id, terminal) in terminals) ReleaseInBackground(id, terminal);
    }
}

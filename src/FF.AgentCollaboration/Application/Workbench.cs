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
internal sealed class Workbench(WorkbenchStartup startup, SessionCatalog catalog, ILogger<Workbench> logger, WorkbenchEvents events) : BackgroundService
{
    private string Root => startup.Root;
    private readonly ConcurrentDictionary<string, TerminalSession> terminals = new();
    private readonly ConcurrentDictionary<string, string> wakeErrors = new();
    private readonly ConcurrentDictionary<string, string> waking = new();
    private readonly ConcurrentDictionary<string, byte> updatingEngines = new();
    private readonly SemaphoreSlim lifecycle = new(1);
    private int disposed;

    internal WorkbenchView Snapshot(string? topicId, int recent = 6)
    {
        using var state = new WorkbenchStore(Root);
        var pendingMcps = state.McpRevisions();
        var windows = state.Windows().Select(agent =>
        {
            terminals.TryGetValue(agent.Id, out var terminal);
            var (online, inputReady, activity) = terminal?.ViewState() ?? (false, false, "offline");
            return new WindowView(agent with { Online = online }, terminal?.Generation, terminal?.ExitCode,
                inputReady, activity, state.Inbox(agent.Id), wakeErrors.GetValueOrDefault(agent.Id), online && pendingMcps.ContainsKey(agent.Id));
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
        var (current, projects) = state.ProjectList(topics.Select(t => t.Project).Concat(windows.Select(w => w.Agent.Project)));
        return new(current, projects, topics, windows, topicId, topicId == null ? [] : state.Timeline(topicId, recent),
            state.MentionShortcut(), state.RecentHandoffs(), state.FederationSummary());
    }

    internal TerminalSession Terminal(string id) => terminals.TryGetValue(id, out var terminal)
        ? terminal : throw new WorkbenchError("终端未启动", WorkbenchFailure.NotFound);

    internal async Task UpdateEngine(string engine, CancellationToken ct)
    {
        await lifecycle.WaitAsync(ct);
        try
        {
            using var state = new WorkbenchStore(Root);
            if (state.Windows().Any(w => w.Engine == engine && terminals.TryGetValue(w.Id, out var t) && t.Online))
                throw new WorkbenchError("请先关闭使用此引擎的终端，再更新。Agent 和会话会保留。", WorkbenchFailure.Conflict);
            if (!updatingEngines.TryAdd(engine, 0)) throw new WorkbenchError("此引擎正在更新", WorkbenchFailure.Conflict);
        }
        finally { lifecycle.Release(); }
        try { await EngineInstallation.Update(engine, ct); }
        finally { updatingEngines.TryRemove(engine, out _); }
    }

    internal async Task ChangeHistory(string windowId, string action, HistoryAction input, CancellationToken ct)
    {
        if (action is not ("archive" or "restore" or "delete")) throw new WorkbenchError("未知会话操作");
        string session = WorkbenchInput.Text(input.Session, "会话 ID", 256);
        await lifecycle.WaitAsync(ct);
        try
        {
            using var state = new WorkbenchStore(Root);
            var window = state.Window(windowId);
            if (input.Engine != null) window = window with { Engine = EngineNames.Require(input.Engine) };
            using var engine = Engine.Create(window.Engine, window.Project);
            if (!engine.Capabilities.Allows(action)) throw new WorkbenchError("这个引擎不支持该会话操作");
            var history = await catalog.Sessions(window.Engine, window.Project, ct, input.Archived, includeBound: true);
            var item = history.FirstOrDefault(s => s.Id == session) ?? throw new WorkbenchError("该会话已移除，或不在当前列表中", WorkbenchFailure.Conflict);
            if (item.MayBeInUse) throw new WorkbenchError("会话已绑定或可能仍在运行，请先切换或关闭对应终端", WorkbenchFailure.Conflict);
            if ((action == "archive" && input.Archived) || (action == "restore" && !input.Archived))
                throw new WorkbenchError("会话归档状态已变化，请刷新列表", WorkbenchFailure.Conflict);
            if (engine.Capabilities.NativeArchive || action == "delete")
            {
                using var deadline = SessionCatalog.Deadline(ct);
                await engine.ChangeHistory(item.Id, action, deadline.Token);
            }
            // Copilot 无原生归档 API；仅归档标记留在工作台，删除始终由原生 CLI 执行。
            if (!engine.Capabilities.NativeArchive) state.SetArchive(window.Engine, item.Id, action == "archive");
        }
        finally { lifecycle.Release(); }
    }

    private async Task<HistorySession?> LatestSession(WorkbenchStore state, AgentWindow candidate, CancellationToken request)
    {
        using var engine = Engine.Create(candidate.Engine, candidate.Project);
        if (!engine.Capabilities.Allows("latest")) return null;
        // 原生新会话尚未绑定历史 ID 时，避免自动接管同项目的另一个在线窗格。
        if (state.Windows().Any(window => window.Id != candidate.Id && window.Engine == candidate.Engine && window.Session == null &&
            ProjectFolders.Same(window.Project, candidate.Project) && terminals.TryGetValue(window.Id, out var terminal) && terminal.Online))
            return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(request);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        try { return (await catalog.Sessions(candidate.Engine, candidate.Project, deadline.Token)).FirstOrDefault(); }
        catch (Exception error) when (!request.IsCancellationRequested)
        {
            logger.LogDebug("{Engine} history lookup unavailable: {Type}", candidate.Engine, error.GetType().Name);
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
            AutoReceive: true, Online: false, TopicId: topic.Id) { Approval = ValidateApproval(request.Approval), Role = WorkbenchInput.AgentRole(request.Role) };
        await EngineAuthentication.Require(engineName, project, ct);
        await lifecycle.WaitAsync(ct);
        try
        {
            if (session != null)
            {
                var history = await catalog.Sessions(engineName, project, ct, includeBound: request.ForkSession);
                var source = history.FirstOrDefault(item => item.Id == session)
                    ?? throw new WorkbenchError("这个历史会话不属于所选项目，或已在工作台打开", WorkbenchFailure.Conflict);
                window = window with { SessionTitle = source.Name };
            }
            if (session == null && !request.NewSession)
            {
                var latest = await LatestSession(state, window, ct);
                window = window with { Session = latest?.Id, SessionTitle = latest?.Name };
            }
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

    internal async Task Start(string id, CancellationToken ct, bool fromInbox = false)
    {
        await lifecycle.WaitAsync(ct);
        try
        {
            using var state = new WorkbenchStore(Root);
            if (fromInbox && !state.TakeWake(id)) return;
            var window = state.Window(id);
            if (terminals.TryGetValue(id, out var existing) && existing.Online) return;
            wakeErrors.TryRemove(id, out _);
            if (fromInbox && window.Session != null)
            {
                var account = await EngineAuthentication.Check(window.Engine, window.Project, ct);
                if (account.Status == "signed-out") throw new WorkbenchError("请先登录引擎", WorkbenchFailure.LoginRequired);
                if (account.Status == "unavailable") throw new WorkbenchError("引擎不可用", WorkbenchFailure.Unavailable);
            }
            if (window.Session == null)
            {
                // 只有未绑定的旧窗格需要选择会话。已有会话直接在自己的 ConPTY 中恢复。
                await EngineAuthentication.Require(window.Engine, window.Project, ct);
                if (!window.NewSessionPending)
                {
                    var latest = await LatestSession(state, window, ct);
                    window = window with { Session = latest?.Id, SessionTitle = latest?.Name };
                }
            }
            await Open(state, window, ct, reopening: true);
            if (fromInbox) waking[id] = terminals[id].Generation;
            state.ClearWake(id);
        }
        catch (Exception error) when (fromInbox && !ct.IsCancellationRequested)
        {
            using var state = new WorkbenchStore(Root);
            state.ClearWake(id);
            logger.LogWarning("Inbox wake failed for {WindowId}: {Reason}", id, error.GetType().Name);
            wakeErrors[id] = error is WorkbenchError { Failure: WorkbenchFailure.LoginRequired }
                ? "自动启动失败，请先登录引擎。消息已保留。"
                : "自动启动失败，请检查引擎安装与登录状态，再点击电源键。消息已保留。";
        }
        finally { lifecycle.Release(); }
    }

    internal async Task ConfigureAgent(string id, AgentConfiguration input, CancellationToken ct)
    {
        await lifecycle.WaitAsync(ct);
        try
        {
            using var state = new WorkbenchStore(Root);
            var current = state.Window(id);
            var name = WorkbenchInput.AgentName(input.Name);
            var role = WorkbenchInput.AgentRole(input.Role);
            string engine = input.Engine == null ? current.Engine : EngineNames.Require(input.Engine);
            bool changingEngine = engine != current.Engine;
            if (state.Windows().Any(w => w.Id != id && w.TopicId == current.TopicId && string.Equals(w.Name,name,StringComparison.OrdinalIgnoreCase)))
                throw new WorkbenchError("这个话题已有同名 Agent", WorkbenchFailure.Conflict);
            var updated = current with {Name = name, Role = role};
            if (changingEngine)
            {
                if (EngineExecutable.TryFindExecutable(engine) == null) throw new WorkbenchError("请先安装所选引擎", WorkbenchFailure.Unavailable);
                using var adapter = Engine.Create(engine, current.Project);
                var mode = EmptyToNull(input.Mode);
                if (mode != null && !adapter.Capabilities.Modes.Any(m => m.Id == mode)) throw new WorkbenchError("该引擎不支持所选模式");
                updated = updated with { Engine = engine, Model = Option(input.Model,"模型"), Effort = Option(input.Effort,"推理档位"),
                    Mode = mode, Approval = ValidateApproval(input.Approval), Session = null, SessionTitle = null, NewSessionPending = true };
            }
            if (input.SessionMode is not (null or "new" or "resume") || (input.SessionMode == "resume") != !string.IsNullOrWhiteSpace(input.SessionId))
                throw new WorkbenchError("请选择新建或一个历史会话");
            bool switchSession = input.SessionMode == "new" || input.SessionMode == "resume" && input.SessionId != updated.Session;
            if (switchSession)
            {
                string? session = EmptyToNull(input.SessionId);
                var binding = session == null ? null : (await catalog.Sessions(engine, current.Project, ct)).FirstOrDefault(s => s.Id == session)
                    ?? throw new WorkbenchError("会话不可用或已绑定", WorkbenchFailure.Conflict);
                updated = updated with { Session = session, SessionTitle = binding?.Name, NewSessionPending = session == null };
            }
            if ((changingEngine || switchSession) && terminals.TryGetValue(id,out var terminal) && terminal.Online)
            {
                await EngineAuthentication.Require(engine, current.Project, ct);
                await Open(state, updated, ct, restartingForSettings:true, applyConfiguration:true);
            }
            else state.SaveConfiguration(updated);
            events.Changed();
        }
        catch (SqliteException e) when (e.SqliteExtendedErrorCode == 2067)
        {
            throw new WorkbenchError("名称或会话已被其他窗口使用", WorkbenchFailure.Conflict);
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
            await EngineAuthentication.Require(window.Engine, window.Project, ct);
            if (session != null && session != window.Session)
            {
                var history = await catalog.Sessions(window.Engine, window.Project, ct);
                var chosen = history.FirstOrDefault(item => item.Id == session)
                    ?? throw new WorkbenchError("这个历史会话不属于所选项目，或已在工作台打开", WorkbenchFailure.Conflict);
                window = window with { SessionTitle = chosen.Name };
            }
            if (session != null && state.Windows().Any(other => other.Id != id && other.Engine == window.Engine && other.Session == session))
                throw new WorkbenchError("这个历史会话已在工作台打开", WorkbenchFailure.Conflict);
            if (session != null && session == window.Session && terminals.TryGetValue(id, out var current) && current.Online)
                return;

            if (!terminals.TryGetValue(id, out var active) || !active.Online)
            {
                state.SaveConfiguration(window with { Session = session, SessionTitle = session == null ? null : window.SessionTitle,
                    NewSessionPending = request.NewSession });
                events.Changed();
                return;
            }
            await Open(state, window with { Session = session }, ct, restartingForSettings:true);
        }
        finally { lifecycle.Release(); }
    }

    private async Task<AgentWindow> Open(WorkbenchStore state, AgentWindow window, CancellationToken ct, string? forkSource = null,
        bool reopening = false, bool restartingForSettings = false, ShareSettings? shareOnRestart = null, bool applyConfiguration = false)
    {
        if (updatingEngines.ContainsKey(window.Engine)) throw new WorkbenchError("此引擎正在更新，请稍后打开", WorkbenchFailure.Conflict);
        using var engine = Engine.Create(window.Engine, window.Project);
        if (!engine.Capabilities.Allows(forkSource != null ? "fork" : window.Session == null ? "new" : "resume"))
            throw new WorkbenchError("这个引擎不支持所选的会话操作");
        if (window.Mode != null && !engine.Capabilities.Modes.Any(mode => mode.Id == window.Mode))
            throw new WorkbenchError("这个引擎不支持所选的初始模式");
        string? newTitle = null;
        if (window.Session == null)
        {
            newTitle = state.Topic(window.TopicId).Name + "-" + window.Name;
            using var timeout = SessionCatalog.Deadline(ct);
            string id = await engine.NewSession(newTitle, window.Model, forkSource, timeout.Token);
            if (id == forkSource || !engine.ValidSessionId(id)) throw new WorkbenchError("引擎未返回独立的会话编号", WorkbenchFailure.Unavailable);
            window = window with { Session = id, SessionTitle = newTitle };
        }
        else if (window.SessionTitle == null)
        {
            // 升级前的窗口只保存了会话 ID。仅在打开该窗口时补取一次已有标题。
            using var lookup = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lookup.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                var history = await catalog.Sessions(window.Engine, window.Project, lookup.Token, includeBound: true);
                window = window with { SessionTitle = history.FirstOrDefault(item => item.Id == window.Session)?.Name };
            }
            catch (Exception error) when (!ct.IsCancellationRequested)
            {
                logger.LogDebug("Session title unavailable: {Type}", error.GetType().Name);
            }
        }
        var grant = WorkbenchStore.NewGrant();
        var mcp = new EngineMcp(Root, "workbench-mcp", grant, CollaborationTools.Names, events.NotifyPort);
        using var launchTimeout = SessionCatalog.Deadline(ct);
        var forLaunch = window;
        if (window.Approval != null && window.Engine != "copilot")
        {
            var policy = StartupApproval.For(window.Engine);
            forLaunch = window with { Mode = window.Approval == "ask" ? policy.Ask : policy.Auto };
        }
        terminals.TryGetValue(window.Id, out var previous);
        bool reopeningEmpty = reopening && newTitle == null && window.Session != null && window.Engine switch {
            "claude" => restartingForSettings && !ClaudeHistory.HasConversation(window.Project, window.Session, ct),
            "qoder" => !(await engine.ListSessions(launchTimeout.Token, false)).Any(s => s.Id == window.Session),
            _ => false
        };
        if (reopeningEmpty)
        {
            // Qoder 首轮前没有会话文件。关闭再打开时可复用原编号，已有记录仍交给原生恢复。
            bool unused = previous?.CanReopenEmpty ?? engine is QoderEngine;
            if (!unused || (engine is QoderEngine qoder && qoder.HasTranscript(window.Session!)))
                throw new WorkbenchError("未找到可恢复的原生对话内容，请核对会话记录", WorkbenchFailure.Conflict);
            newTitle = window.SessionTitle ?? state.Topic(window.TopicId).Name + "-" + window.Name;
        }
        bool reader=state.DedicatedReader(window.Id);
        if (shareOnRestart != null) reader=shareOnRestart.RestrictedTerminal(reader);
        long revision = state.McpRevisions().GetValueOrDefault(window.Id);
        if (!reader)
        {
            if(window.Engine is "claude" or "zcode")
            {
                bool claude=window.Engine=="claude";
                string extra=claude?EngineSkillRoots.ClaudeOverlay(Root,window.Project):EngineSkillRoots.ZCodeGlobal(Root);
                if(Directory.Exists(extra) && state.Skills().Any(s=>s.Enabled && (s.Engines.Length==0 || s.Engines.Contains(window.Engine)) &&
                    (claude ? s.ProjectPath!=null && ProjectFolders.Same(s.ProjectPath,window.Project) : s.Scope=="global")))
                {
                    TopicArchive.CheckPath(extra);
                    mcp=mcp with {ExtraSkillRoot=extra};
                }
            }
            var candidates = state.ManagedMcps().Where(m => m.Applies(window.Engine,window.Project) && McpConfig.Supports(window.Engine,m.Transport)).ToList();
            if (candidates.Count > 0)
            {
                var names=NativeMcpInventory.Reserved(window.Engine,window.Project);
                if(!names.Contains(null)) mcp=mcp with { Managed=candidates.Where(m=>!names.Contains(m.Name)).ToArray() };
            }
        }
        if(reader) forLaunch=forLaunch with {Approval=null, Mode=window.Engine switch
            { "codex" or "traecli"=>"on-request","copilot"=>null,_=>"plan" }};
        var launch = await engine.PrepareTerminal(forLaunch, mcp, newTitle, forkSource, reopening, launchTimeout.Token);
        if(reader && window.Engine is "codex" or "traecli") launch.Arguments.AddRange(["--sandbox","read-only"]);
        if(reader && window.Engine=="copilot") launch.Arguments.AddRange(["--mode=plan","--available-tools","view","grep","glob","lx_collab"]);
        string executable=EngineExecutable.FindExecutable(window.Engine);
        if(OperatingSystem.IsWindows() && mcp.Managed.Count>0)
        {
            var command=FF.AgentCollaboration.Infrastructure.Windows.WindowsCommand.Resolve(executable,launch.Arguments);
            int limit=Path.GetFileName(command.FileName).Equals("cmd.exe",StringComparison.OrdinalIgnoreCase)?8191:32766;
            if(command.Arguments.Length+command.FileName.Length+3>limit)
                throw new WorkbenchError("此引擎的启动参数超过 Windows 长度限制，请缩短或停用部分托管 MCP 后重试");
        }
        if (previous != null)
        {
            if (restartingForSettings && previous.Online)
                await previous.CloseForSettingsChange(() => state.Offline(window.Id), reopeningEmpty, ct);
            else
            {
                state.Offline(window.Id);
                await Task.Run(previous.Dispose);
            }
        }
        var previousBinding = state.Window(window.Id);
        // 撤销旧进程的窗口凭据后，才能发布与新进程启动参数相符的共享权限。
        if (shareOnRestart != null) state.SetShare(window.Id, shareOnRestart);
        if (applyConfiguration) state.StartConfigured(window, grant);
        else state.Start(window, grant);
        try
        {
            terminals[window.Id] = new TerminalSession(window.Engine, executable, launch.Arguments,
                ProjectFolders.Resolve(window.Project), grant, 100, 30, events.Changed, launch.Environment, engine.SubmissionReceipts(window));
            state.AppliedMcps(window.Id, revision);
            return window with { Online = true };
        }
        catch
        {
            if (applyConfiguration) state.SaveConfiguration(previousBinding);
            state.RestoreSession(previousBinding);
            if (shareOnRestart != null) throw new WorkbenchError("协作设置已保存，终端启动失败。请检查引擎后点击电源键重试。", WorkbenchFailure.Unavailable);
            throw;
        }
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? ValidateApproval(string? value) => value is null or "ask" or "auto" ? value : throw new WorkbenchError("请选择需要审批或自动审批");

    internal async Task ChangeApprovalMode(string id, ChangeApproval request, CancellationToken ct)
    {
        string policy = ValidateApproval(request.Approval) ?? throw new WorkbenchError("请选择审批模式");
        await lifecycle.WaitAsync(ct);
        try
        {
            using var state = new WorkbenchStore(Root);
            var window = state.Window(id);
            terminals.TryGetValue(id, out var current);
            if ((current?.Online == true ? current.Generation : null) != request.Terminal)
                throw new WorkbenchError("终端状态已变化，请重新选择", WorkbenchFailure.Conflict);
            if (window.Session == null) await EngineAuthentication.Require(window.Engine, window.Project, ct);
            await Open(state, window with { Approval = policy }, ct, reopening: true, restartingForSettings: true);
        }
        finally { lifecycle.Release(); }
    }
    internal async Task<bool> ConfigureSharing(string id, ShareSettings input, CancellationToken ct)
    {
        input.Validate();
        await lifecycle.WaitAsync(ct);
        try
        {
            using var state = new WorkbenchStore(Root);
            var window = state.Window(id);
            bool current = state.DedicatedReader(id);
            if (input.RestrictedTerminal(current) != current)
            {
                terminals.TryGetValue(id, out var terminal);
                var generation = terminal?.Online == true ? terminal.Generation : null;
                if (generation != input.RestartTerminal)
                    throw new WorkbenchError("当前终端已变化，请重新确认保存并重启", WorkbenchFailure.Conflict);
                if (generation != null)
                {
                    await Open(state, window, ct, reopening: true, restartingForSettings: true, shareOnRestart: input);
                    return true;
                }
            }
            state.SetShare(id, input);
            return false;
        }
        finally { lifecycle.Release(); }
    }

    private static string? Option(string? option, string label)
    {
        string? value = EmptyToNull(option);
        if (value != null && (value.Length > 140 || value.StartsWith('-') ||
            value.Any(character => !(char.IsAsciiLetterOrDigit(character) || "._:/-".Contains(character)))))
            throw new WorkbenchError($"{label}格式不正确");
        return value;
    }

    internal async Task Stop(string id, CancellationToken ct, bool removeWindow = false)
    {
        await lifecycle.WaitAsync(ct);
        try
        {
            using var state = new WorkbenchStore(Root);
            state.Window(id);
            state.StopWindow(id);
            wakeErrors.TryRemove(id, out _);
            waking.TryRemove(id, out _);
            if (terminals.TryGetValue(id, out var terminal)) await Task.Run(terminal.Dispose);
            if (removeWindow) state.Close(id);
            terminals.TryRemove(id, out _);
        }
        finally { lifecycle.Release(); }
    }

    internal async Task Toggle(string id, ChangeAutoReceive request, CancellationToken ct)
    {
        if ((request.Enabled is null && request.Mode is null) || request.Mode is not (null or "queued" or "immediate"))
            throw new WorkbenchError("请选择排队接收或立即接收");
        await lifecycle.WaitAsync(ct);
        try
        {
            using var state = new WorkbenchStore(Root);
            state.Window(id);
            state.AutoReceive(id, request.Enabled, request.Mode);
            if (request.Enabled == false) state.ClearWake(id);
            events.Changed();
        }
        finally { lifecycle.Release(); }
    }

    // 同一把生命周期锁保护会话切换与插入，避免向刚更换的原生会话写入旧选择。
    internal async Task InsertMention(string id, MentionTarget selection, CancellationToken ct)
    {
        await lifecycle.WaitAsync(ct);
        try
        {
            var source = Terminal(id);
            var target = Terminal(WorkbenchInput.Id(selection.TargetId));
            if (id == selection.TargetId || !source.Online || !target.Online ||
                source.Generation != selection.SourceTerminal || target.Generation != selection.TargetTerminal)
                throw new WorkbenchError("窗口已离线或切换会话，请重新选择", WorkbenchFailure.Conflict);
            using var store = new WorkbenchStore(Root);
            var peer = store.Window(selection.TargetId);
            string prefix = store.Window(id).TopicId == peer.TopicId ? "" : store.Topic(peer.TopicId).Name + "/";
            await source.Input(Encoding.UTF8.GetBytes("@" + prefix + peer.Name + " "), ct);
        }
        finally { lifecycle.Release(); }
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

    internal async Task OpenApprovalMenu(string id, string generation, CancellationToken ct)
    {
        await lifecycle.WaitAsync(ct);
        try
        {
            var terminal = Terminal(id);
            if (terminal.Generation != generation) throw new WorkbenchError("终端已切换，请重新选择审批模式", WorkbenchFailure.Conflict);
            using var store = new WorkbenchStore(Root);
            var window = store.Window(id);
            using var engine = Engine.Create(window.Engine, window.Project);
            await terminal.OpenApprovalMenu(engine.ApprovalKeys, ct);
        }
        finally { lifecycle.Release(); }
    }

    private async Task<bool> Deliver(TerminalSession terminal, InboxItem message, bool automatic, CancellationToken ct, bool immediate = false)
    {
        using var state = new WorkbenchStore(Root);
        bool claimed = false;
        try
        {
            if(message.Source=="peer-mailbox") await state.BeginPeerAudit(message.Id,ct);
            bool written = await terminal.Deliver(message, automatic, () =>
            {
                claimed = state.ClaimMessage(message.Id, automatic, immediate);
                if (claimed)
                {
                    waking.TryRemove(new KeyValuePair<string,string>(message.Target, terminal.Generation));
                    events.RefreshPages();
                }
                return claimed;
            }, ct, immediate);
            if (written) state.FinishMessage(message.Id, "delivered");
            return written;
        }
        catch
        {
            if (claimed) state.FinishMessage(message.Id, "delivery_uncertain");
            throw;
        }
        finally { if (claimed) events.Changed(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await startup.Ready.WaitAsync(stoppingToken);
        // 同一连接的 data_version 可识别老版本 MCP 提交或通知丢失的情况。
        using var state = new WorkbenchStore(Root);
        long version = state.DataVersion;
        using var fallback = new Timer(_ => events.Wake(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        events.Wake();
        try
        {
            while (await events.WaitAsync(stoppingToken))
            {
                long current = state.DataVersion;
                if (current != version) events.RefreshPages();
                version = current;
                var windows = state.Windows().ToDictionary(w => w.Id);
                await state.CheckPeerAudits(id=>terminals.TryGetValue(id,out var t)?t.ViewState().Activity:"offline",stoppingToken);
                foreach (var terminal in terminals.Values)
                    foreach (string message in terminal.ReconcileReceipts())
                        if (state.ConfirmLateReceipt(message))
                        {
                            terminal.RemoveReceipt(message);
                            events.RefreshPages();
                        }
                foreach (var window in windows.Values)
                    if (window.Online && terminals.TryGetValue(window.Id, out var terminal) && !terminal.Online)
                        await ReleaseExited(state, window.Id, terminal, stoppingToken);
                foreach (var id in state.WakeTargets())
                {
                    await Start(id, stoppingToken, fromInbox: true);
                    events.RefreshPages();
                }
                foreach (var entry in state.Automatic())
                {
                    if (!windows.TryGetValue(entry.Target, out var recipient) || !terminals.TryGetValue(entry.Target, out var terminal)) continue;
                    bool immediate = recipient.ReceiveMode == "immediate";
                    if (!terminal.CanDeliver(automatic: true, immediate)) continue;
                    try { await Deliver(terminal, entry, automatic: true, stoppingToken, immediate); }
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
                if (waking.TryRemove(new KeyValuePair<string,string>(id, terminal.Generation)) && state.Inbox(id).Any(i => i.State == "pending"))
                {
                    wakeErrors[id] = "自动启动的终端已退出。请检查引擎登录状态后重新打开，消息已保留。";
                    state.StopWindow(id);
                }
                else state.Offline(id);
                await Task.Run(() => ReleaseInBackground(id, terminal));
                events.RefreshPages();
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

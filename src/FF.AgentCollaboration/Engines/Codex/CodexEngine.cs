using System.Text.Json.Nodes;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Files;

namespace FF.AgentCollaboration.Engines;

internal sealed class CodexEngine(string cwd) : RpcEngine
{
    internal override EngineCapabilities Capabilities => Profile;
    private static readonly EngineCapabilities Profile = new("审批策略",
        [new("on-request", "按需请求审批"), new("never", "不发起审批请求")],
        ["new", "resume", "fork", "archive", "restore", "delete"], NativeArchive: true);

    internal async Task Start(EngineMcp? mcp, CancellationToken ct)
    {
        Connect(new RpcPipe(EngineExecutable.FindExecutable("codex"), ["app-server", "--listen", "stdio://"], cwd, mcp?.Grant));
        await Rpc.Request("initialize", new { clientInfo = Client, capabilities = new { experimentalApi = true } }, ct);
        await Rpc.Notify("initialized", ct);
    }

    internal override async Task<NativeSession> Open(string? existingId, string? model, EngineMcp mcp, string instructions,
        string sandbox, string approvalPolicy, CancellationToken ct)
    {
        await Start(mcp, ct);
        var parameters = new Dictionary<string, object?>
        {
            ["cwd"] = cwd,
            ["sandbox"] = sandbox,
            ["approvalPolicy"] = approvalPolicy,
            ["developerInstructions"] = instructions,
            ["config"] = new
            {
                mcp_servers = new
                {
                    lx_collab = new
                    {
                        command = mcp.Command,
                        args = mcp.Arguments,
                        env_vars = new[] { "LX_P0_WINDOW_GRANT" },
                        enabled_tools = mcp.Tools,
                        default_tools_approval_mode = "approve"
                    }
                }
            }
        };
        if (existingId != null) parameters["threadId"] = existingId;
        if (model != null) parameters["model"] = model;
        var reply = await Rpc.Request(existingId == null ? "thread/start" : "thread/resume", parameters, ct);
        var id = (string)reply["thread"]!["id"]!;
        var actualModel = (string?)reply["model"];
        if (model != null && model != actualModel)
            throw new InvalidOperationException("Codex 未启用选定模型，请刷新模型列表");
        return new NativeSession(id, actualModel);
    }

    internal override async Task<IReadOnlyList<ModelChoice>> ListModels(CancellationToken ct)
    {
        await Start(null, ct);
        var models = new List<ModelChoice>();
        await foreach (var model in Items(cursor => Rpc.Request("model/list", new { cursor, limit = 100 }, ct), "data"))
            models.Add(new((string)model!["id"]!, (string?)model["displayName"] ?? (string)model["id"]!,
            (bool?)model["isDefault"] == true, Efforts(model["supportedReasoningEfforts"], "reasoningEffort")));
        return models;
    }

    internal override async Task<EngineQuota> ReadQuota(CancellationToken ct)
    {
        await Start(null, ct);
        var result = await Rpc.Request("account/rateLimits/read", new { }, ct);
        var values = result["rateLimits"];
        var primary = WindowQuota(values?["primary"]);
        var secondary = WindowQuota(values?["secondary"]);
        var snapshot = values?["credits"];
        var balance = (string?)snapshot?["balance"];
        bool unlimited = (bool?)snapshot?["unlimited"] == true;
        var credits = unlimited || !string.IsNullOrWhiteSpace(balance) ? new QuotaCredits(balance, unlimited) : null;
        var timestamp = DateTimeOffset.UtcNow;
        return primary == null && secondary == null && credits == null
            ? new(false, "Codex 未返回账号额度", CheckedAt: timestamp)
            : new(true, null, primary, secondary, credits, timestamp);
    }

    private static QuotaWindow? WindowQuota(JsonNode? window)
    {
        if ((int?)window?["usedPercent"] is not int used) return null;
        return new(used, Math.Clamp(100 - used, 0, 100), (int?)window?["windowDurationMins"], (long?)window?["resetsAt"]);
    }

    internal override async Task<IReadOnlyList<HistorySession>> ListSessions(CancellationToken ct, bool archived)
    {
        await Start(null, ct);
        var sessions = new List<HistorySession>();
        await foreach (var value in Items(cursor => Rpc.Request("thread/list", new { cwd, cursor, limit = 100, archived }, ct), "data"))
        {
            string? project = (string?)value["cwd"];
            if (project == null || !ProjectFolders.Same(Path.TrimEndingDirectorySeparator(project), cwd)) continue;
            string id = (string)value["id"]!;
            string? title = (string?)value["name"];
            if (string.IsNullOrWhiteSpace(title)) title = (string?)value["preview"];
            string? updated = value["updatedAt"] is JsonValue time && time.TryGetValue<long>(out long seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds).ToString("O") : null;
            sessions.Add(new HistorySession(id, string.IsNullOrWhiteSpace(title) ? id : title, project, updated));
        }
        return sessions;
    }

    protected override bool ProbeWriter(string id)
    {
        string home = Environment.GetEnvironmentVariable("CODEX_HOME") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        string path = Path.Combine(home, "thread-writer-locks", id + ".lock");
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        if (OperatingSystem.IsWindows()) file.Lock(0, 1);
        return false; // 关闭句柄立即释放探测锁，不创建或删除锁文件。
    }

    internal override async Task<string> NewSession(string title, string? model, string? source, CancellationToken ct)
    {
        await Start(null, ct);
        var reply = source == null ? await Rpc.Request("thread/start", new { cwd, model }, ct)
            : await Rpc.Request("thread/fork", new { threadId = source, cwd, model }, ct);
        string? id = (string?)reply["thread"]?["id"];
        if (!Guid.TryParse(id, out _) || id == source) throw new WorkbenchError("Codex 未返回独立的会话编号", WorkbenchFailure.Unavailable);
        await Rpc.Request("thread/name/set", new { threadId = id, name = title }, ct);
        return id;
    }

    internal override async Task ChangeHistory(string id, string action, CancellationToken ct)
    {
        await Start(null, ct);
        string method = action == "restore" ? "unarchive" : action;
        await Rpc.Request("thread/" + method, new { threadId = id }, ct);
    }

    internal override async Task Turn(string session, string eventId, string prompt, Action<string?> started, Action<string> delta,
        Action? toolCompleted, CancellationToken ct)
    {
        string? turnId = null;
        string? failure = null;
        bool observedStart = false, finished = false;
        void ObserveStart(string? nativeId)
        {
            if (observedStart) return;
            observedStart = true;
            started(nativeId);
        }

        Rpc.Notice = (method, parameters) =>
        {
            if ((string?)parameters?["threadId"] == session)
            {
                if (method == "turn/started")
                {
                    turnId = (string?)parameters?["turn"]?["id"];
                    ObserveStart(turnId);
                }
                if (method == "turn/completed" && (string?)parameters?["turn"]?["id"] == turnId)
                {
                    finished = true;
                    if ((string?)parameters?["turn"]?["status"] != "completed")
                        failure = parameters?["turn"]?["error"]?["message"]?.ToString() ?? "Codex 轮次未完成";
                }
                if (method == "item/agentMessage/delta" && (string?)parameters?["delta"] is { } text)
                    delta(text);
                if (method == "item/completed" && (string?)parameters?["item"]?["type"] == "mcpToolCall")
                    toolCompleted?.Invoke();
            }
        };

        try
        {
            var input = new[] { new { type = "text", text = $"[LX-EVENT:{eventId}]{prompt}" } };
            var reply = await Rpc.Request("turn/start", new { threadId = session, clientUserMessageId = eventId, input }, ct);
            turnId ??= (string?)reply["turn"]?["id"];
            while (!finished) await Rpc.Read(ct);
            if (failure != null) throw new EngineTurnFailed(failure);
        }
        finally
        {
            Rpc.Notice = (_, _) => { };
        }
    }

    internal async Task<int> CountCodexInputs(string session, string eventId, CancellationToken ct)
    {
        var response = await Rpc.Request("thread/read", new { threadId = session, includeTurns = true }, ct);
        return response["thread"]!["turns"]!.AsArray().SelectMany(turn => turn!["items"]!.AsArray()).Count(item =>
            (string?)item?["type"] == "userMessage" && ((string?)item?["clientId"] == eventId ||
            item?["content"]?.AsArray().Any(part => ((string?)part?["text"])?.StartsWith($"[LX-EVENT:{eventId}]", StringComparison.Ordinal) == true) == true));
    }

    internal override List<string> NativeArguments(AgentWindow window, EngineMcp mcp, string? newTitle, string? forkSource)
    {
        var args = new List<string>();
        args.AddRange(["resume", window.Session!]);
        args.AddRange(["--no-daemon", "-C", window.Project]);
        if (window.Model != null) args.AddRange(["-m", window.Model]);
        if (window.Effort != null) args.AddRange(["-c", "model_reasoning_effort=" + Toml(window.Effort)]);
        if (window.Mode != null) args.AddRange(["-c", "approval_policy=" + Toml(window.Mode)]);
        const string server = "mcp_servers.lx_collab.";
        args.AddRange(["-c", server + "command=" + Toml(mcp.Command),
            "-c", server + "args=[" + string.Join(',', mcp.Arguments.Select(Toml)) + "]",
            "-c", server + "env_vars=[\"LX_P0_WINDOW_GRANT\"]",
            "-c", server + "default_tools_approval_mode=\"approve\""]);
        return args;
    }

    private static string Toml(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}

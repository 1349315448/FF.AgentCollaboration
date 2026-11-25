using System.Text.Json;
using System.Text.Json.Nodes;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Files;

namespace FF.AgentCollaboration.Engines;

internal sealed class CopilotEngine(string cwd) : RpcEngine
{
    internal override EngineCapabilities Capabilities => Profile;
    private static readonly EngineCapabilities Profile = new("运行模式",
        [new("agent", "Agent"), new("autopilot", "Autopilot")],
        ["new", "latest", "resume", "fork", "archive", "restore", "delete"], NativeArchive: false);

    private async Task Start(EngineMcp? mcp, CancellationToken ct)
    {
        var arguments = new List<string>
        {
            "--acp", "--no-auto-update", "--disable-builtin-mcps", "--log-level", "none"
        };
        if (mcp != null)
        {
            if (mcp.Verb == "mcp") arguments.Add("--no-custom-instructions");
            var config = JsonSerializer.Serialize(new
            {
                mcpServers = new
                {
                    lx_collab = new
                    {
                        type = "local",
                        command = mcp.Command,
                        args = mcp.Arguments,
                        tools = mcp.Tools,
                        env = new { LX_P0_WINDOW_GRANT = "${LX_P0_WINDOW_GRANT}" }
                    }
                }
            });
            arguments.AddRange(["--allow-tool=lx_collab", "--additional-mcp-config", config]);
        }
        Connect(new RpcPipe(EngineExecutable.FindExecutable("copilot"), arguments, cwd, mcp?.Grant));
        await Rpc.Request("initialize", new { protocolVersion = 1, clientInfo = Client, clientCapabilities = new { } }, ct);
    }

    internal override async Task<NativeSession> Open(string? existingId, string? model, EngineMcp mcp, string instructions,
        string sandbox, string approvalPolicy, CancellationToken ct)
    {
        await Start(mcp, ct);
        var sessionParams = new Dictionary<string, object?> { ["cwd"] = cwd, ["mcpServers"] = Array.Empty<object>() };
        if (existingId != null) sessionParams["sessionId"] = existingId;
        var response = await Rpc.Request(existingId == null ? "session/new" : "session/load", sessionParams, ct);
        var sessionId = existingId ?? (string)response["sessionId"]!;
        var models = response["models"];
        var currentModel = (string?)models?["currentModelId"];
        if (model != null && model != currentModel)
        {
            if (models != null && !(models["availableModels"]?.AsArray().Any(option => (string?)option?["modelId"] == model) ?? false))
                throw new InvalidOperationException($"当前 Copilot 会话未提供模型 {model}");
            await Rpc.Request("session/set_model", new { sessionId, modelId = model }, ct);
        }
        return new NativeSession(sessionId, model ?? currentModel);
    }

    internal override async Task<IReadOnlyList<ModelChoice>> ListModels(CancellationToken ct)
    {
        var models = new List<ModelChoice>();
        var reply = await CopilotSdk("models.list", new { }, ct);
        foreach (var model in reply["models"]!.AsArray())
            models.Add(new((string)model!["id"]!, (string?)model["name"] ?? (string)model["id"]!, false,
                Efforts(model["supportedReasoningEfforts"])));
        return models;
    }

    private Task<JsonNode> CopilotSdk(string method, object parameters, CancellationToken ct)
    {
        Connect(new RpcPipe(EngineExecutable.FindExecutable("copilot"), ["--headless", "--stdio", "--no-auto-update", "--log-level", "none"],
            cwd, null, contentLength: true));
        return Rpc.Request(method, parameters, ct);
    }

    internal override async Task<EngineQuota> ReadQuota(CancellationToken ct)
    {
        var quota = await CopilotSdk("account.getQuota", new { }, ct);
        var premium = quota["quotaSnapshots"]?["premium_interactions"];
        var now = DateTimeOffset.UtcNow;
        if ((bool?)premium?["isUnlimitedEntitlement"] == true)
            return new(true, "高级交互额度不限量", CheckedAt: now, Unlimited: true);
        if ((decimal?)premium?["remainingPercentage"] is not decimal remaining || remaining is < 0 or > 100)
            return new(false, "Copilot 未返回高级交互剩余额度", CheckedAt: now);
        // 部分账号用本次查询时间填充 resetDate，只有将来的重置时间才展示。
        long? reset = DateTimeOffset.TryParse((string?)premium?["resetDate"], out var date) && date > now
            ? date.ToUnixTimeSeconds() : null;
        return new(true, null, new(100 - remaining, remaining, null, reset), CheckedAt: now);
    }

    internal override async Task<IReadOnlyList<HistorySession>> ListSessions(CancellationToken ct, bool archived)
    {
        await Start(null, ct);
        var sessions = new List<HistorySession>();
        await foreach (var value in Items(cursor => Rpc.Request("session/list", new { cwd, cursor }, ct), "sessions"))
        {
            string? project = (string?)value["cwd"];
            if (project == null || !ProjectFolders.Same(Path.TrimEndingDirectorySeparator(project), cwd)) continue;
            string id = (string)value["sessionId"]!;
            string? title = (string?)value["title"];
            string? updated = (string?)value["updatedAt"];
            sessions.Add(new HistorySession(id, string.IsNullOrWhiteSpace(title) ? id : title, project, updated));
        }
        return sessions;
    }

    protected override bool ProbeWriter(string id)
    {
        string home = Environment.GetEnvironmentVariable("COPILOT_HOME") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot");
        foreach (string marker in Directory.EnumerateFiles(Path.Combine(home, "session-state", id), "inuse.*.lock"))
        {
            var parts = Path.GetFileName(marker).Split('.');
            if (parts.Length != 3 || !int.TryParse(parts[1], out int pid)) continue;
            try
            {
                using var writer = System.Diagnostics.Process.GetProcessById(pid);
                if (!writer.HasExited) return true;
            }
            catch (ArgumentException) { } // 已退出进程遗留的标记不代表会话仍在使用。
        }
        return false;
    }

    internal override async Task<string> NewSession(string title, string? model, string? source, CancellationToken ct)
    {
        if (source == null) return Guid.NewGuid().ToString("D");
        var fork = await CopilotSdk("sessions.fork", new { sessionId = source, name = title }, ct);
        return (string?)fork["sessionId"] ?? throw new WorkbenchError("Copilot 未返回分支编号", WorkbenchFailure.Unavailable);
    }

    internal override async Task ChangeHistory(string id, string action, CancellationToken ct)
    {
        var result = await CopilotSdk("session.delete", new { sessionId = id }, ct);
        if ((bool?)result["success"] != true)
            throw new WorkbenchError((string?)result["error"] ?? "Copilot 未能删除会话", WorkbenchFailure.Conflict);
    }

    internal override async Task Turn(string session, string eventId, string prompt, Action<string?> started, Action<string> delta,
        Action? toolCompleted, CancellationToken ct)
    {
        bool observedStart = false;
        Rpc.Notice = (method, parameters) =>
        {
            if (method != "session/update" || (string?)parameters?["sessionId"] != session) return;
            var update = parameters?["update"];
            var kind = (string?)update?["sessionUpdate"];
            if (!observedStart && kind is ("agent_message_chunk" or "agent_thought_chunk" or "tool_call" or "tool_call_update"))
            {
                observedStart = true;
                started(null);
            }
            if (kind == "agent_message_chunk" && (string?)update?["content"]?["text"] is { } text) delta(text);
        };
        try
        {
            var input = new[] { new { type = "text", text = $"[LX-EVENT:{eventId}]{prompt}" } };
            var reply = await Rpc.Request("session/prompt", new { sessionId = session, prompt = input }, ct);
            if ((string?)reply["stopReason"] != "end_turn") throw new EngineTurnFailed($"Copilot 轮次结束原因：{reply["stopReason"]}");
        }
        finally
        {
            Rpc.Notice = (_, _) => { };
        }
    }

    internal override List<string> NativeArguments(AgentWindow window, EngineMcp mcp, string? newTitle, string? forkSource)
    {
        var args = new List<string>();
        if (newTitle != null && forkSource == null) args.AddRange(["--session-id=" + window.Session, "--name=" + newTitle]);
        else args.Add("--resume=" + window.Session);
        if (window.Model != null) args.AddRange(["--model", window.Model]);
        if (window.Effort != null) args.AddRange(["--reasoning-effort", window.Effort]);
        if (window.Mode == "agent") args.Add("--mode=interactive");
        if (window.Mode == "autopilot") args.Add("--autopilot");
        var config = JsonSerializer.Serialize(new
        {
            mcpServers = new
            {
                lx_collab = new
                {
                    type = "local",
                    command = mcp.Command,
                    args = mcp.Arguments,
                    tools = mcp.Tools,
                    env = new { LX_P0_WINDOW_GRANT = "${LX_P0_WINDOW_GRANT}" }
                }
            }
        });
        args.AddRange(["--additional-mcp-config", config, "--allow-tool=lx_collab"]);
        return args;
    }
}

using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Files;

namespace FF.AgentCollaboration.Engines;

internal sealed class CodexEngine(string cwd, string executable = "codex") : RpcEngine
{
    internal override EngineCapabilities Capabilities => Profile;
    internal override async Task<TerminalLaunch> PrepareTerminal(AgentWindow window, EngineMcp mcp, string? newTitle,
        string? forkSource, bool reopening, CancellationToken ct)
    {
        var launch = await base.PrepareTerminal(window, mcp, newTitle, forkSource, reopening, ct);
        // TRAE's queue talks to its shared daemon. This workbench starts its TUI with --no-daemon.
        if (executable != "codex") return launch;
        string binary = EngineExecutable.FindExecutable(executable);
        if (!Guid.TryParseExact(window.Session, "D", out _) || !Path.GetExtension(binary).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return launch;
        string session = window.Session!;
        var (status, commands) = await EngineAuthentication.RunCommand(binary, cwd, ["--help"], ct);
        if (status != 0 || !commands.Split('\n').Any(line => line.TrimStart().StartsWith("queue ", StringComparison.Ordinal))) return launch;
        var (code, help) = await EngineAuthentication.RunCommand(binary, cwd, ["queue", "--help"], ct);
        if (code != 0 || !help.Contains("--thread", StringComparison.Ordinal) || !help.Contains("--message", StringComparison.Ordinal))
            return launch;
        return launch with { Enqueue = async (body, token) =>
        {
            var result = await EngineAuthentication.RunCommand(binary, cwd, ["queue", "--thread", session, "--message", body], token);
            if (result.Code != 0) throw new IOException("原生会话未确认收件，请核对投递状态");
        } };
    }

    internal override ISubmissionReceiptSource? SubmissionReceipts(AgentWindow window)
    {
        if (executable != "codex" || !Guid.TryParseExact(window.Session, "D", out _)) return null;
        string id = window.Session!;
        string home = Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(
            Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        string? path = null;
        return new JsonlSubmissionReceipts(() => path ??= FindRollout(home, id), (record, envelope) =>
            JsonlSubmissionReceipts.StringIs(record, "type", "response_item") &&
            record.TryGetProperty("payload", out var payload) && payload.ValueKind == System.Text.Json.JsonValueKind.Object &&
            JsonlSubmissionReceipts.StringIs(payload, "type", "message") && JsonlSubmissionReceipts.IsUserEnvelope(payload, envelope),
            record => JsonlSubmissionReceipts.StringIs(record, "type", "session_meta") &&
                record.TryGetProperty("payload", out var meta) && meta.ValueKind == System.Text.Json.JsonValueKind.Object &&
                JsonlSubmissionReceipts.StringIs(meta, "id", id));
    }

    private static string? FindRollout(string home, string id)
    {
        if (!Directory.Exists(home)) return null;
        string sessions = Path.Combine(home, "sessions");
        foreach (var index in Directory.EnumerateFiles(home, "state_*.sqlite").OrderDescending().Take(3))
        {
            try
            {
                using var db = new SqliteConnection(new SqliteConnectionStringBuilder {
                    DataSource = index, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1 }.ToString());
                db.Open();
                using var query = db.CreateCommand();
                query.CommandText = "SELECT rollout_path FROM threads WHERE id=$id";
                query.Parameters.AddWithValue("$id", id);
                if (query.ExecuteScalar() is not string file) continue;
                // Native Windows stores absolute paths with an extended-length prefix in the index.
                if (file.StartsWith(@"\\?\", StringComparison.Ordinal)) file = file[4..];
                if (Path.IsPathFullyQualified(file) && Path.GetFullPath(file).StartsWith(Path.GetFullPath(sessions) +
                    Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(file).EndsWith("-" + id + ".jsonl", StringComparison.OrdinalIgnoreCase)) return file;
            }
            catch (SqliteException) { }
        }
        // UUIDv7 encodes creation time. Look only in its local/UTC date buckets if the CLI has no readable index yet.
        var guid = Guid.Parse(id);
        if (guid.Version != 7) return null;
        var instant = DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(guid.ToString("N")[..12], 16));
        var local = instant.LocalDateTime;
        foreach (var day in new[] { local, instant.UtcDateTime }.Select(d => d.ToString("yyyy/MM/dd", System.Globalization.CultureInfo.InvariantCulture)).Distinct())
        {
            var bucket = Path.Combine(sessions, day);
            if (Directory.Exists(bucket) && Directory.EnumerateFiles(bucket, "*-" + id + ".jsonl").FirstOrDefault() is { } path) return path;
        }
        return Path.Combine(sessions, local.ToString("yyyy/MM/dd", System.Globalization.CultureInfo.InvariantCulture),
            "rollout-" + local.ToString("yyyy-MM-ddTHH-mm-ss", System.Globalization.CultureInfo.InvariantCulture) + "-" + id + ".jsonl");
    }
    internal override async Task<EngineAccount> Account(CancellationToken ct)
    {
        var (code, output) = await EngineAuthentication.Command(executable, cwd, ["login", "status"], ct);
        if (output.Contains("not logged in", StringComparison.OrdinalIgnoreCase) || output.Contains("未登录")) return new("signed-out");
        return new(code == 0 ? "authenticated" : "unknown");
    }
    private static readonly EngineCapabilities Profile = new("审批策略",
        [new("on-request", "按需请求审批"), new("never", "不发起审批请求")],
        ["new", "resume", "fork", "archive", "restore", "delete"], NativeArchive: true);

    internal async Task Start(EngineMcp? mcp, CancellationToken ct)
    {
        Connect(new RpcPipe(EngineExecutable.FindExecutable(executable), ["app-server", "--listen", "stdio://"], cwd, mcp?.Grant));
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
            ? new(false, $"{executable} 未返回账号额度", CheckedAt: timestamp)
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
        bool trae = executable == "traecli";
        string home = Environment.GetEnvironmentVariable(trae ? "TRAE_HOME" : "CODEX_HOME") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), trae ? ".trae" : ".codex");
        string path = Path.Combine(home, "thread-writer-locks", id + ".lock");
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        if (OperatingSystem.IsWindows()) file.Lock(0, 1);
        return false; // 关闭句柄立即释放探测锁，不创建或删除锁文件。
    }

    internal override async Task<string> NewSession(string title, string? model, string? source, CancellationToken ct)
    {
        await Start(null, ct);
        JsonNode reply;
        try
        {
            reply = source == null ? await Rpc.Request("thread/start", new { cwd, model }, ct)
                : await Rpc.Request("thread/fork", new { threadId = source, cwd, model }, ct);
        }
        catch (EngineRpcError error) when (executable == "traecli" &&
            error.RemoteMessage.Contains("No Trae auth token available", StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkbenchError("TRAE 登录已失效，请重新登录后选择会话", WorkbenchFailure.LoginRequired);
        }
        string? id = (string?)reply["thread"]?["id"];
        if (!Guid.TryParse(id, out _) || id == source) throw new WorkbenchError($"{executable} 未返回独立的会话编号", WorkbenchFailure.Unavailable);
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
        McpConfig.AppendCodex(args,mcp);
        return args;
    }

    private static string Toml(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}

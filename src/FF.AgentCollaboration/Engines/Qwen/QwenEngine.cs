using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Files;
using FF.AgentCollaboration.Infrastructure.Windows;

namespace FF.AgentCollaboration.Engines;

internal sealed class QwenEngine(string cwd) : ConfiguredTerminalEngine
{
    protected override string ModeOption => "--approval-mode";
    internal override string[] LoginArguments => ["--chat-recording=false"];
    internal override IReadOnlyDictionary<string, string> LoginEnvironment => new Dictionary<string, string> { ["NO_COLOR"] = "" };
    internal override async Task<EngineAccount> Account(CancellationToken ct)
    {
        string? reportedSource = null;
        using var rpc = new RpcPipe(EngineExecutable.FindExecutable("qwen"), ["--acp"], cwd, null);
        using var stop = ct.Register(rpc.Abort);
        await rpc.Request("initialize", new { protocolVersion = 1, clientCapabilities = new { } }, ct);
        try
        {
            var check = await rpc.Request("qwen/status/workspace/preflight", new { }, ct);
            var cell = (check["cells"] as JsonArray)?.FirstOrDefault(c => (string?)c?["kind"] == "auth");
            if ((string?)cell?["status"] == "ok" && cell?["detail"]?["hasToken"]?.ToJsonString() == "true") return new("configured");
            if ((string?)cell?["status"] == "warning") return new("signed-out");
            if ((string?)cell?["status"] != "unknown") return new("unknown");
            reportedSource = (string?)cell?["detail"]?["source"];
            if (reportedSource is not ("qwen-oauth" or "qwen-subscription")) return new("unknown");
        }
        catch (EngineRpcError error) when (error.Code == -32601) { /* 旧版没有工作区预检。 */ }
        // 旧版和原生不提供状态的 OAuth 按所选来源读取，不向模型发送测试请求。
        var home = Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".qwen");
        var settings = EngineAuthentication.Settings(Path.Combine(home, "settings.json"));
        var auth = settings?["security"]?["auth"];
        string? type = reportedSource ?? (string?)auth?["selectedType"];
        string? nativeKey = type switch {
            null or "openai" or "openai-responses" => "OPENAI_API_KEY", "anthropic" => "ANTHROPIC_API_KEY",
            "gemini" => "GEMINI_API_KEY", "vertex-ai" => "GOOGLE_API_KEY", _ => null };
        string? model = (string?)settings?["model"]?["name"];
        var providers = type == null ? null : settings?["modelProviders"]?[type] as JsonArray;
        var active = providers?.FirstOrDefault(item => model != null && (string?)item?["id"] == model);
        var key = (string?)active?["envKey"] ?? nativeKey;
        if (reportedSource == null && (EngineAuthentication.HasKey(settings, key) || (nativeKey != null && !string.IsNullOrWhiteSpace((string?)auth?["apiKey"]))))
            return new("configured");
        if (type is null or "qwen-subscription" or "qwen-oauth")
        {
            var credentials = EngineAuthentication.Settings(Path.Combine(home, type == "qwen-subscription" ? "subscription_credentials.json" : "oauth_creds.json"));
            if (!string.IsNullOrWhiteSpace((string?)credentials?["refresh_token"]) ||
                (!string.IsNullOrWhiteSpace((string?)credentials?["access_token"]) && (long?)credentials?["expiry_date"] > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
                return new("configured");
        }
        return new(type == "vertex-ai" ? "unknown" : "signed-out");
    }
    internal override string ApprovalKeys => "\t"; // Windows 原生空输入行用 Tab 切换审批模式。
    protected override string QuotaGuide => "百炼 Coding Plan 剩余额度请到百炼控制台的 Coding Plan 页面查看。终端 /stats（部分版本也支持 /usage）显示本机使用统计。";
    internal override EngineCapabilities Capabilities => new("审批模式",
        [new("default", "默认审批"), new("plan", "计划"), new("auto-edit", "自动编辑")],
        ["new", "resume", "archive", "restore"], NativeArchive: false, Models: false, Efforts: false);
    internal override Task<IReadOnlyList<ModelChoice>> ListModels(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ModelChoice>>([]);

    internal override async Task<IReadOnlyList<HistorySession>> ListSessions(CancellationToken ct, bool archived)
    {
        string help = await Output(["--help"], ct);
        if (!help.Split('\n').Any(line => line.TrimStart().StartsWith("qwen sessions ", StringComparison.Ordinal)))
            return await AcpSessions(ct).ToListAsync(ct);
        var active = (await Records(["sessions", "ps", "--json"], ct))
            .Select(row => (string?)row["sessionId"]).ToHashSet();
        var sessions = new List<HistorySession>();
        foreach (var row in await Records(["sessions", "list", "--json", "--limit", "1000"], ct))
        {
            string? id = (string?)row["sessionId"], project = (string?)row["cwd"];
            if (!Guid.TryParse(id, out _) || project == null || !ProjectFolders.Same(project, cwd)) continue;
            string title = (string?)row["customTitle"] ?? (string?)row["prompt"] ?? id!;
            sessions.Add(new(id!, title, cwd, row["startTime"]?.ToString()) { MayBeInUse = active.Contains(id) });
        }
        return sessions;
    }

    internal override async Task<TerminalLaunch> PrepareTerminal(AgentWindow window, EngineMcp mcp, string? newTitle,
        string? forkSource, bool reopening, CancellationToken ct)
    {
        string help = await Output(["--help"], ct);
        bool nativeMcp = help.Contains("--mcp-config", StringComparison.Ordinal);
        // 两代 CLI 在首轮落盘前关闭后都无法 --resume。重开空窗格时仍沿用原 ID。
        bool fresh = newTitle != null || (reopening && !await AcpSessions(ct).AnyAsync(s => s.Id == window.Session, ct));
        var args = SessionArguments(window, fresh, forkSource, nativeMcp ? mcp : null);
        return new TerminalLaunch(args, nativeMcp ? null : LegacyMcpEnvironment(window.Id, mcp));
    }

    private IReadOnlyDictionary<string, string> LegacyMcpEnvironment(string windowId, EngineMcp mcp)
    {
        const string systemKey = "QWEN_CODE_SYSTEM_SETTINGS_PATH", defaultsKey = "QWEN_CODE_SYSTEM_DEFAULTS_PATH";
        string PathSetting(string key, string fallback) => Path.GetFullPath(Environment.GetEnvironmentVariable(key) ?? fallback, cwd);
        string system = PathSetting(systemKey, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "qwen-code", "settings.json"));
        string defaults = PathSetting(defaultsKey, Path.Combine(Path.GetDirectoryName(system)!, "system-defaults.json"));
        // 只占用空配置层，个人配置和管理员已有的两个配置文件都不改写、不复制。
        string path = Path.Combine(mcp.Root, "engine-profiles", windowId, "qwen-mcp.json");
        // Qwen 0.14's no-color theme has an empty gradient and crashes its interactive welcome screen.
        var environment = new Dictionary<string, string> { ["NO_COLOR"] = "" };
        if (!File.Exists(system))
        {
            environment[systemKey] = path;
            environment[defaultsKey] = defaults;
        }
        else if (!File.Exists(defaults)) environment[defaultsKey] = path;
        else throw new WorkbenchError("此版本千问的两层系统配置均已使用，请升级到支持 --mcp-config 的 Qwen Code");
        TopicArchive.CheckPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var server = new { command = mcp.Command, args = mcp.Arguments,
            env = new { LX_P0_WINDOW_GRANT = "${LX_P0_WINDOW_GRANT}" } };
        TopicArchive.Publish(path, JsonSerializer.Serialize(new { mcpServers = new { lx_collab = server } }), overwrite: true);
        return environment;
    }

    private async IAsyncEnumerable<HistorySession> AcpSessions([EnumeratorCancellation] CancellationToken ct)
    {
        using var rpc = new RpcPipe(EngineExecutable.FindExecutable("qwen"), ["--acp"], cwd, null);
        await rpc.Request("initialize", new { protocolVersion = 1, clientCapabilities = new { } }, ct);
        string? cursor = null;
        do
        {
            object query = cursor == null ? new { cwd } : new { cwd, cursor };
            var page = await rpc.Request("session/list", query, ct);
            foreach (var item in page["sessions"]!.AsArray())
            {
                if ((string?)item?["sessionId"] is { } id && Guid.TryParse(id, out _) &&
                    (string?)item["cwd"] is { } project && ProjectFolders.Same(project, cwd))
                    yield return new(id, (string?)item["title"] ?? id, project, (string?)item["updatedAt"]);
            }
            cursor = (string?)page["nextCursor"];
        } while (!string.IsNullOrEmpty(cursor));
    }

    private async Task<List<JsonNode>> Records(string[] arguments, CancellationToken ct)
    {
        var records = new List<JsonNode>();
        foreach (var line in (await Output(arguments, ct)).Split('\n'))
            if (!string.IsNullOrWhiteSpace(line) && JsonNode.Parse(line) is JsonObject row) records.Add(row);
        return records;
    }

    private async Task<string> Output(string[] arguments, CancellationToken ct)
    {
        var start = WindowsCommand.Resolve(EngineExecutable.FindExecutable("qwen"), arguments);
        start.WorkingDirectory = cwd;
        start.RedirectStandardOutput = start.RedirectStandardError = true;
        start.StandardOutputEncoding = Encoding.UTF8;
        using var process = Process.Start(start) ?? throw new WorkbenchError("无法启动千问 CLI");
        process.BeginErrorReadLine();
        try
        {
            string output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0) throw new WorkbenchError("千问 CLI 未能运行，请检查该版本需要的 Node.js 和启动入口");
            return output;
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }
}

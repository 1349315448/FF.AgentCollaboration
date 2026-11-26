using System.Text.Json;
using System.Text.Json.Nodes;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Files;

namespace FF.AgentCollaboration.Engines;

internal sealed class QoderEngine(string cwd) : ConfiguredTerminalEngine
{
    protected override string ModeOption => "--permission-mode";
    protected override string QuotaGuide => "请在 Qoder CLI 中输入 /usage 查看当前账户用量。";
    internal override EngineCapabilities Capabilities => new("审批权限",
        [new("default", "需要审批"), new("auto", "自动审批"), new("accept_edits", "自动接受编辑"), new("plan", "计划")],
        ["new", "resume", "fork", "archive", "restore"], NativeArchive:false, Efforts:false);

    internal override async Task<EngineAccount> Account(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("QODER_PERSONAL_ACCESS_TOKEN"))) return new("configured");
        var (code, output) = await EngineAuthentication.Command("qoder", cwd, ["status", "--output", "json"], ct);
        if (code != 0) return new("unknown");
        return new((bool?)JsonNode.Parse(output)?["logged_in"] switch { true => "authenticated", false => "signed-out", null => "unknown" });
    }

    internal override async Task<IReadOnlyList<ModelChoice>> ListModels(CancellationToken ct)
    {
        var (code, output) = await EngineAuthentication.Command("qoder", cwd, ["--list-models"], ct);
        if (code != 0) throw new WorkbenchError("无法读取此账号的 Qoder 模型列表");
        var ids = output.Split(['\r','\n'], StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim())
            .SkipWhile(line => line != "MODEL").Skip(1)
            .Where(id => id.Length > 0 && char.IsAsciiLetterOrDigit(id[0]) && id.All(c => char.IsAsciiLetterOrDigit(c) || "._:/-".Contains(c)));
        return ids.Distinct().Select(id => new ModelChoice(id, id, id == "auto", [])).ToArray();
    }

    internal override async Task<IReadOnlyList<HistorySession>> ListSessions(CancellationToken ct, bool archived)
    {
        using var rpc = new RpcPipe(EngineExecutable.FindExecutable("qoder"), ["--acp"], cwd, null);
        using var stop = ct.Register(rpc.Abort);
        await rpc.Request("initialize", new { protocolVersion = 1, clientCapabilities = new { } }, ct);
        var sessions = new List<HistorySession>();
        string? cursor = null;
        do
        {
            var page = await rpc.Request("session/list", new { cwd, cursor }, ct);
            foreach (var item in page["sessions"]?.AsArray() ?? [])
                if ((string?)item?["sessionId"] is { } id && ValidSessionId(id) &&
                    (string?)item["cwd"] is { } project && Path.IsPathFullyQualified(project) && ProjectFolders.Same(project, cwd))
                    sessions.Add(new(id, (string?)item["title"] ?? id, project, (string?)item["updatedAt"]));
            cursor = (string?)page["nextCursor"];
        } while (!string.IsNullOrEmpty(cursor));
        return sessions;
    }

    internal override List<string> NativeArguments(AgentWindow window, EngineMcp mcp, string? newTitle, string? forkSource)
    {
        var args = SessionArguments(window, newTitle != null, forkSource, null);
        if (newTitle != null) args.AddRange(["--name", newTitle]);
        // Qoder 的 --mcp-config 接收文件路径；授权只由当前进程环境提供。
        string file = Path.Combine(mcp.Root, "engine-profiles", window.Id, "qoder-mcp.json");
        TopicArchive.CheckPath(file);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var server = new { command = mcp.Command, args = mcp.Arguments, env = new { LX_P0_WINDOW_GRANT = "${LX_P0_WINDOW_GRANT}" } };
        TopicArchive.Publish(file, JsonSerializer.Serialize(new { mcpServers = mcp.Servers("qoder",server) }), overwrite:true);
        args.AddRange(["--mcp-config", file, "--settings", """{"ui":{"terminalBuffer":false,"useAlternateBuffer":false}}"""]);
        return args;
    }

    internal bool HasTranscript(string session)
    {
        if (!ValidSessionId(session)) return false;
        var home = Environment.GetEnvironmentVariable("QODER_CONFIG_DIR") ??
            Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".qoder");
        var projects = Path.Combine(home, "projects");
        return Directory.Exists(projects) && Directory.EnumerateDirectories(projects)
            .Any(dir => !File.GetAttributes(dir).HasFlag(FileAttributes.ReparsePoint) && File.Exists(Path.Combine(dir, session + ".jsonl")));
    }
}

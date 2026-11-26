using System.Text.Json;
using System.Text.Json.Nodes;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Files;

namespace FF.AgentCollaboration.Engines;

internal sealed class ZCodeEngine(string cwd) : Engine
{
    internal override Task<EngineAccount> Account(CancellationToken ct)
    {
        string home = Environment.GetEnvironmentVariable("ZCODE_DATA_BASE_DIR") ?? Environment.GetEnvironmentVariable("USERPROFILE") ??
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string file = Environment.GetEnvironmentVariable("ZCODE_CREDENTIALS_FILE") ?? Path.Combine(home, ".zcode", "v2", "credentials.json");
        if (!Path.IsPathFullyQualified(file)) return Task.FromResult(new EngineAccount("unknown"));
        var vault = EngineAuthentication.Settings(file) as JsonObject;
        // 共享凭据库的字段名可读，值由原生 CLI 解密。忽略退出后保留的 revision 和其它工具的 OAuth。
        bool hasOAuth = vault != null && (vault["oauth:bigmodel:access_token"] != null ||
            (vault["oauth:active_provider"] != null && vault["oauth:zai:access_token"] != null));
        bool configured = hasOAuth || (vault != null && vault.Any(entry => {
            const string prefix = "account-provider:", suffix = ":auth-source";
            if (!entry.Key.StartsWith(prefix, StringComparison.Ordinal) || !entry.Key.EndsWith(suffix, StringComparison.Ordinal) || entry.Value == null) return false;
            string provider = entry.Key[prefix.Length..^suffix.Length];
            string manualPrefix = $"account-provider:coding-plan:{provider}:account:key-";
            return vault[prefix + provider + ":identity"] != null && vault.Any(key => key.Value != null &&
                key.Key.StartsWith(manualPrefix, StringComparison.Ordinal) && key.Key.EndsWith(":api-key", StringComparison.Ordinal) &&
                key.Key.Length == manualPrefix.Length + 24 + ":api-key".Length &&
                key.Key.Substring(manualPrefix.Length, 24).All(char.IsAsciiHexDigit));
        }));
        return Task.FromResult(new EngineAccount(configured ? "configured" : "signed-out"));
    }
    internal override string ApprovalKeys => "/mode "; // 输入前缀就显示模式列表，由用户选中并回车。
    internal override EngineCapabilities Capabilities => new("运行模式",
        [new("build", "构建"), new("plan", "计划"), new("edit", "编辑"), new("guarded", "受限")],
        ["new", "resume", "archive", "restore"], NativeArchive: false, Models: false, Efforts: false);
    internal override bool ValidSessionId(string id) => id.Length is > 0 and <= 128 && char.IsAsciiLetterOrDigit(id[0]) &&
        id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    internal override Task<IReadOnlyList<ModelChoice>> ListModels(CancellationToken ct) => Task.FromResult<IReadOnlyList<ModelChoice>>([]);
    internal override Task<EngineQuota> ReadQuota(CancellationToken ct) => Task.FromResult(
        new EngineQuota(false, "请在 ZCode 客户端查看当前连接的用量；个人 GLM Coding Plan 也可用智谱官方用量查询插件查看。", CheckedAt: DateTimeOffset.UtcNow) { Status = "unsupported" });
    internal override Task ChangeHistory(string id, string action, CancellationToken ct) => throw new WorkbenchError("ZCode 未开放原生历史删除");

    private RpcPipe Connect() => new(EngineExecutable.FindExecutable("zcode"), ["app-server", "--stdio", "--cwd", cwd], cwd, null, bareEnvelope: true);

    internal override async Task<string> NewSession(string title, string? model, string? source, CancellationToken ct)
    {
        using var rpc = Connect();
        async Task<JsonNode?> Command(string type, string? sessionId, object payload)
        {
            var reply = await rpc.Request("v4/command", new
            {
                commandId = Guid.NewGuid().ToString("D"), clientId = "ff-agent", sessionId, type, payload,
                issuedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }, ct);
            if ((string?)reply["status"] != "accepted") throw new WorkbenchError($"ZCode 未接受 {type} 会话操作");
            return reply["result"];
        }
        var result = await Command("createSession", null, new { workspaceId = cwd, permissionScope = "session", mcpServers = Array.Empty<object>() });
        if ((string?)result?["sessionId"] is not { } id || !ValidSessionId(id)) throw new WorkbenchError("ZCode 未返回有效会话编号");
        await Command("renameSession", id, new { title });
        return id;
    }

    internal override async Task<IReadOnlyList<HistorySession>> ListSessions(CancellationToken ct, bool archived)
    {
        using var rpc = Connect();
        var reply = await rpc.Request("session/list", new { workspace = new { workspacePath = cwd, workspaceKey = cwd }, limit = 200 }, ct);
        var result = new List<HistorySession>();
        foreach (var item in reply["sessions"]!.AsArray())
        {
            if ((string?)item?["sessionId"] is not { } id || !ValidSessionId(id) ||
                (string?)item["workspace"]?["workspacePath"] is not { } project || !ProjectFolders.Same(cwd, project)) continue;
            long? time = (long?)item["updatedAt"];
            result.Add(new(id, (string?)item["title"] ?? id, project,
                time is long ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("O") : null));
        }
        return result;
    }

    internal override List<string> NativeArguments(AgentWindow window, EngineMcp mcp, string? newTitle, string? forkSource)
    {
        var args = new List<string> { "--cwd", cwd, "--resume", window.Session! };
        if (window.Mode != null) args.AddRange(["--mode", window.Mode]);
        return args;
    }

    internal override IReadOnlyDictionary<string, string> TerminalEnvironment(AgentWindow window, EngineMcp mcp)
    {
        // ZCode discovers MCP only in its project or user config. Use a workbench-owned
        // profile for this window while keeping the real model credentials and native history.
        string home = Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string original = Path.Combine(home, ".zcode", "cli", "config.json");
        string? userDatabase = File.Exists(original)
            ? (string?)JsonNode.Parse(File.ReadAllText(original), documentOptions: new JsonDocumentOptions
                { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })?["storage"]?["sessionDbPath"] : null;
        string AbsoluteHome(string path) => path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)
            ? Path.Combine(home, path[2..]) : path;
        string database = AbsoluteHome(string.IsNullOrWhiteSpace(userDatabase) ? "~/.zcode/cli/db/db.sqlite" : userDatabase);
        string profile = Path.Combine(mcp.Root, "engine-profiles", window.Id);
        string directory = Path.Combine(profile, ".zcode", "cli");
        TopicArchive.CheckPath(directory);
        Directory.CreateDirectory(directory);
        var server = new { type = "stdio", command = mcp.Command, args = mcp.Arguments, enabled = true, protocolVersion = "legacy" };
        var config = Path.Combine(directory, "config.json");
        TopicArchive.CheckPath(config);
        var settings=new Dictionary<string,object> { ["features"]=new {mcp=true}, ["storage"]=new {sessionDbPath=database},
            ["mcp"]=new {servers=mcp.Servers("zcode",server)} };
        if(mcp.ExtraSkillRoot!=null) settings["skills"]=new {roots=new[]{mcp.ExtraSkillRoot}};
        File.WriteAllText(config,JsonSerializer.Serialize(settings));
        var environment = new Dictionary<string, string>
        {
            ["USERPROFILE"] = profile,
            ["ZCODE_DATA_BASE_DIR"] = Environment.GetEnvironmentVariable("ZCODE_DATA_BASE_DIR") ?? home
        };
        foreach (string key in new[] { "ZCODE_SESSION_DB_PATH", "ZCODE_SESSION_DB" })
            if (Environment.GetEnvironmentVariable(key) is { Length: > 0 } path) environment[key] = AbsoluteHome(path);
        return environment;
    }
}

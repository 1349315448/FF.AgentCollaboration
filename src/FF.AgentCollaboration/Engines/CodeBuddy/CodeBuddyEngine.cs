using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Files;

namespace FF.AgentCollaboration.Engines;

internal sealed class CodeBuddyEngine(string cwd) : ConfiguredTerminalEngine
{
    protected override string ModeOption => "--permission-mode";
    internal override string[] LoginArguments => ["--no-session-persistence"];
    internal override async Task<EngineAccount> Account(CancellationToken ct)
    {
        var home = Environment.GetEnvironmentVariable("CODEBUDDY_CONFIG_DIR") ?? Path.Combine(
            Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codebuddy");
        foreach (var path in new[] { Path.Combine(cwd, ".codebuddy", "settings.local.json"), Path.Combine(cwd, ".codebuddy", "settings.json"), Path.Combine(home, "settings.json") })
        {
            var settings = EngineAuthentication.Settings(path);
            if (EngineAuthentication.HasKey(settings, "CODEBUDDY_AUTH_TOKEN") ||
                (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CODEBUDDY_API_KEY_DISABLED")) && EngineAuthentication.HasKey(settings, "CODEBUDDY_API_KEY")) ||
                (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CODEBUDDY_API_KEY_HELPER_DISABLED")) && !string.IsNullOrWhiteSpace((string?)settings?["apiKeyHelper"])))
                return new("configured");
        }
        using var rpc = new RpcPipe(EngineExecutable.FindExecutable("codebuddy"), ["--acp"], cwd, null);
        using var stop = ct.Register(rpc.Abort);
        await rpc.Request("initialize", new { protocolVersion = 1, clientCapabilities = new { } }, ct);
        var user = await rpc.Request("_codebuddy.ai/getUserInfo", new { }, ct);
        return new(string.IsNullOrWhiteSpace((string?)user["userInfo"]?["userId"]) ? "signed-out" : "authenticated");
    }
    internal override string ApprovalKeys => "\u001b[Z";
    protected override string QuotaGuide => "请在 CodeBuddy 官网的「个人主页 → 套餐与用量」查看可用积分。终端内的 /cost 显示当前会话消耗。";
    internal override EngineCapabilities Capabilities => new("权限模式",
        [new("default", "默认审批"), new("plan", "计划"), new("acceptEdits", "接受编辑")],
        ["new", "resume", "fork", "archive", "restore"], NativeArchive: false, Efforts: false);

    internal override Task<IReadOnlyList<ModelChoice>> ListModels(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ModelChoice>>(new[] { "default-model", "fast-model", "balanced-model", "primary-model", "deep-model" }
            .Select(id => new ModelChoice(id, id, id == "default-model", [])).ToArray());

    internal override Task<IReadOnlyList<HistorySession>> ListSessions(CancellationToken ct, bool archived)
    {
        var home = Environment.GetEnvironmentVariable("CODEBUDDY_CONFIG_DIR") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codebuddy");
        var root = Path.Combine(home, "projects");
        var sessions = new List<HistorySession>();
        if (Directory.Exists(root))
        {
            var active = ClaudeHistory.ActiveSessions(home);
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                ct.ThrowIfCancellationRequested();
                if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) continue;
                foreach (var file in Directory.EnumerateFiles(directory, "*.jsonl"))
                {
                    try
                    {
                        if (ClaudeHistory.Read(file, cwd, ct) is { } item)
                            sessions.Add(item with { MayBeInUse = active.Contains(item.Id) });
                    }
                    catch (FileNotFoundException) { }
                }
            }
        }
        return Task.FromResult<IReadOnlyList<HistorySession>>(sessions);
    }
}

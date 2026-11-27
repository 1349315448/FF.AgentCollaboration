using System.Text.Json;
using FF.AgentCollaboration.Domain;

namespace FF.AgentCollaboration.Engines;

internal sealed class ClaudeEngine(string cwd) : Engine
{
    internal override EngineCapabilities Capabilities => Profile;
    internal override string[] LoginArguments => ["auth", "login"];
    internal override async Task<EngineAccount> Account(CancellationToken ct)
    {
        var (_, output) = await EngineAuthentication.Command("claude", cwd, ["auth", "status"], ct);
        using var result = JsonDocument.Parse(output);
        return new(result.RootElement.GetProperty("loggedIn").GetBoolean() ? "authenticated" : "signed-out");
    }
    internal override string ApprovalKeys => "\u001b[Z"; // Shift+Tab 切换当前会话的审批模式。
    private static readonly EngineCapabilities Profile = new("权限模式",
        [new("default", "默认审批"), new("acceptEdits", "自动接受编辑"), new("plan", "计划"), new("bypassPermissions", "跳过权限确认")],
        ["new", "resume", "fork", "archive", "restore", "delete"], NativeArchive: false);

    internal override IReadOnlyList<ModelChoice> KnownModels { get; } =
        new[] { "sonnet", "opus", "haiku", "claude-sonnet-4-6", "claude-opus-4-7", "claude-haiku-4-5" }
            .Select(id => new ModelChoice(id, id, id == "sonnet", ["low", "medium", "high", "max"])).ToArray();

    internal override Task<EngineQuota> ReadQuota(CancellationToken ct) => Task.FromResult(
        new EngineQuota(false, "请在 Claude Code 终端输入 /usage 查看订阅用量与限额。", CheckedAt: DateTimeOffset.UtcNow) { Status = "unsupported" });

    internal override Task<IReadOnlyList<HistorySession>> ListSessions(CancellationToken ct, bool archived)
    {
        var active = ClaudeHistory.ActiveSessions();
        return Task.FromResult<IReadOnlyList<HistorySession>>(ClaudeHistory.List(cwd, ct)
            .Select(item => item with { MayBeInUse = active.Contains(item.Id) }).ToArray());
    }

    internal override Task ChangeHistory(string id, string action, CancellationToken ct)
    {
        ClaudeHistory.Delete(cwd, id, ct);
        return Task.CompletedTask;
    }

    internal override List<string> NativeArguments(AgentWindow window, EngineMcp mcp, string? newTitle, string? forkSource)
    {
        var args = new List<string>();
        if (forkSource != null) args.AddRange(["--resume", forkSource, "--fork-session"]);
        if (newTitle != null) args.AddRange(["--session-id", window.Session!, "--name", newTitle]);
        else args.AddRange(["--resume", window.Session!]);
        if (window.Model != null) args.AddRange(["--model", window.Model]);
        if (window.Effort != null) args.AddRange(["--effort", window.Effort]);
        if (window.Mode != null) args.AddRange(["--permission-mode", window.Mode]);
        if (mcp.ExtraSkillRoot != null) args.AddRange(["--add-dir",mcp.ExtraSkillRoot]);
        var server = new
        {
            type = "stdio",
            command = mcp.Command,
            args = mcp.Arguments,
            env = new { LX_P0_WINDOW_GRANT = "${LX_P0_WINDOW_GRANT}" }
        };
        args.AddRange(["--mcp-config", JsonSerializer.Serialize(new { mcpServers = mcp.Servers("claude",server) }),
            "--allowedTools", "mcp__lx_collab__*"]);
        return args;
    }
}

using System.Text.Json;
using FF.AgentCollaboration.Domain;

namespace FF.AgentCollaboration.Engines;

internal sealed class ClaudeEngine(string cwd) : Engine
{
    internal override EngineCapabilities Capabilities => Profile;
    private static readonly EngineCapabilities Profile = new("权限模式",
        [new("default", "默认审批"), new("acceptEdits", "自动接受编辑"), new("plan", "计划"), new("bypassPermissions", "跳过权限确认")],
        ["new", "resume", "fork", "archive", "restore", "delete"], NativeArchive: false);

    internal override Task<IReadOnlyList<ModelChoice>> ListModels(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ModelChoice>>(new[] { "sonnet", "opus", "haiku", "claude-sonnet-4-6", "claude-opus-4-7", "claude-haiku-4-5" }
            .Select(id => new ModelChoice(id, id, id == "sonnet", ["low", "medium", "high", "max"])).ToArray());

    internal override Task<EngineQuota> ReadQuota(CancellationToken ct) => Task.FromResult(
        new EngineQuota(false, "Claude Code 暂不支持原生额度查询，可在终端内使用 /usage 查看", CheckedAt: DateTimeOffset.UtcNow) { Status = "unsupported" });

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
        var server = new
        {
            type = "stdio",
            command = mcp.Command,
            args = mcp.Arguments,
            env = new { LX_P0_WINDOW_GRANT = "${LX_P0_WINDOW_GRANT}" }
        };
        args.AddRange(["--mcp-config", JsonSerializer.Serialize(new { mcpServers = new { lx_collab = server } }),
            "--allowedTools", "mcp__lx_collab__*"]);
        return args;
    }
}

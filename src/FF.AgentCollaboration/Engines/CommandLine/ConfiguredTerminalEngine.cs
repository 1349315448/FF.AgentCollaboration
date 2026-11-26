using System.Text.Json;
using FF.AgentCollaboration.Domain;

namespace FF.AgentCollaboration.Engines;

// Both interactive CLIs accept an explicit session id and a launch-scoped MCP definition.
internal abstract class ConfiguredTerminalEngine : Engine
{
    protected abstract string QuotaGuide { get; }
    internal override Task<EngineQuota> ReadQuota(CancellationToken ct) => Task.FromResult(
        new EngineQuota(false, QuotaGuide, CheckedAt: DateTimeOffset.UtcNow) { Status = "unsupported" });

    internal override Task ChangeHistory(string id, string action, CancellationToken ct) =>
        throw new WorkbenchError("该引擎未开放原生历史删除");

    protected abstract string ModeOption { get; }

    internal override List<string> NativeArguments(AgentWindow window, EngineMcp mcp, string? newTitle, string? forkSource) =>
        SessionArguments(window, newTitle != null, forkSource, mcp);

    protected List<string> SessionArguments(AgentWindow window, bool newSession, string? forkSource, EngineMcp? mcp)
    {
        var args = new List<string>();
        if (forkSource != null) args.AddRange(["--resume", forkSource, "--fork-session"]);
        args.AddRange([newSession ? "--session-id" : "--resume", window.Session!]);
        if (window.Mode != null) args.AddRange([ModeOption, window.Mode]);
        if (window.Model != null) args.AddRange(["--model", window.Model]);
        if (mcp == null) return args;
        // Qwen inherits both grants but treats interpolation in --mcp-config as literal text.
        object server = window.Engine == "qwen" ? new { command = mcp.Command, args = mcp.Arguments } :
            new { command = mcp.Command, args = mcp.Arguments, env = new { LX_P0_WINDOW_GRANT = "${LX_P0_WINDOW_GRANT}" } };
        args.AddRange(["--mcp-config", JsonSerializer.Serialize(new { mcpServers = mcp.Servers(window.Engine,server) })]);
        return args;
    }
}

using FF.AgentCollaboration.Domain;

namespace FF.AgentCollaboration.Engines;

// 工作台调用每个引擎的交互终端和历史能力。RPC 验收流程只使用 RpcEngine。
internal abstract class Engine : IDisposable
{
    internal static Engine Create(string name, string cwd) => name switch
    {
        "codex" => new CodexEngine(cwd),
        "copilot" => new CopilotEngine(cwd),
        "claude" => new ClaudeEngine(cwd),
        "codebuddy" => new CodeBuddyEngine(cwd),
        "qwen" => new QwenEngine(cwd),
        "qoder" => new QoderEngine(cwd),
        "traecli" => new CodexEngine(cwd, name),
        "zcode" => new ZCodeEngine(cwd),
        "cursor" => new CursorEngine(cwd),
        "opencode" => new OpenCodeEngine(cwd),
        "antigravity" => new AntigravityEngine(cwd),
        _ => throw new WorkbenchError("请选择受支持的引擎")
    };

    internal abstract EngineCapabilities Capabilities { get; }
    internal virtual bool CreatesSessionBeforeLaunch => true;
    internal virtual bool SupportsDedicatedReader => true;
    internal virtual ISubmissionReceiptSource? SubmissionReceipts(AgentWindow window) => null;
    internal abstract Task<EngineAccount> Account(CancellationToken ct);
    internal virtual string[] LoginArguments => ["login"];
    internal virtual IReadOnlyDictionary<string, string>? LoginEnvironment => null;
    // 指定原生终端的权限入口。末尾回车只执行打开菜单的命令。
    internal virtual string ApprovalKeys => "/permissions\r";
    internal virtual bool ValidSessionId(string id) => Guid.TryParse(id, out _);
    internal virtual IReadOnlyDictionary<string, string>? TerminalEnvironment(AgentWindow window, EngineMcp mcp) => null;
    internal virtual IReadOnlyList<ModelChoice> KnownModels => [];
    internal virtual Task<IReadOnlyList<ModelChoice>> ListModels(CancellationToken ct) => Task.FromResult(KnownModels);
    internal abstract Task<EngineQuota> ReadQuota(CancellationToken ct);
    internal abstract Task<IReadOnlyList<HistorySession>> ListSessions(CancellationToken ct, bool archived);
    internal virtual bool HasNativeWriter(string id) => false;
    internal abstract Task ChangeHistory(string id, string action, CancellationToken ct);
    internal virtual Task<string> NewSession(string title, string? model, string? source, CancellationToken ct) =>
        Task.FromResult(Guid.NewGuid().ToString("D"));
    internal abstract List<string> NativeArguments(AgentWindow window, EngineMcp mcp, string? newTitle, string? forkSource);
    internal virtual async Task<TerminalLaunch> PrepareTerminal(AgentWindow window, EngineMcp mcp, string? newTitle,
        string? forkSource, bool reopening, CancellationToken ct)
    {
        var args = NativeArguments(window, mcp, newTitle, forkSource);
        bool channels = false;
        if (window.Engine == "codebuddy")
        {
            var (code, help) = await EngineAuthentication.Command(window.Engine, window.Project, ["--help"], ct);
            channels = code == 0 && help.Contains("--channels ",StringComparison.Ordinal);
        }
        if (window.Engine == "claude")
        {
            // Claude 从 2.1.80 支持频道；部分版本的 --help 隐藏此预览参数。
            var (code, version) = await EngineAuthentication.Command("claude",window.Project,["--version"],ct);
            channels = code == 0 && Version.TryParse(version.Split(' ')[0],out var installed) && installed >= new Version(2,1,80);
        }
        if (channels) args.InsertRange(0,["--channels","server:lx_collab"]);
        return new TerminalLaunch(args, TerminalEnvironment(window, mcp)) { NativeInbox = channels };
    }
    public virtual void Dispose() { }
}

internal sealed record TerminalLaunch(List<string> Arguments, IReadOnlyDictionary<string, string>? Environment)
{
    internal bool NativeInbox { get; init; }
    internal Func<string, CancellationToken, Task>? Enqueue { get; init; }
}

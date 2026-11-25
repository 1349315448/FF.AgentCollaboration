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
        _ => throw new WorkbenchError("请选择受支持的引擎")
    };

    internal abstract EngineCapabilities Capabilities { get; }
    internal abstract Task<IReadOnlyList<ModelChoice>> ListModels(CancellationToken ct);
    internal abstract Task<EngineQuota> ReadQuota(CancellationToken ct);
    internal abstract Task<IReadOnlyList<HistorySession>> ListSessions(CancellationToken ct, bool archived);
    internal virtual bool HasNativeWriter(string id) => false;
    internal abstract Task ChangeHistory(string id, string action, CancellationToken ct);
    internal virtual Task<string> NewSession(string title, string? model, string? source, CancellationToken ct) =>
        Task.FromResult(Guid.NewGuid().ToString("D"));
    internal abstract List<string> NativeArguments(AgentWindow window, EngineMcp mcp, string? newTitle, string? forkSource);
    public virtual void Dispose() { }
}

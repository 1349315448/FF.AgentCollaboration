using System.Reflection;

namespace FF.AgentCollaboration.Engines;

internal sealed record EngineMode(string Id, string Label);
// 原生启动模式与工具审批在 Copilot 中相互独立。其他引擎通过其模式参数选择策略。
internal sealed record StartupApproval(string Ask, string Auto, string Description)
{
    internal static StartupApproval? For(string engine) => engine switch
    {
        "codex" or "traecli" => new("on-request", "never", "在已有沙箱权限内执行，超出权限的操作会被拒绝。"),
        "copilot" => new("default", "--allow-all-tools", "允许工具自动运行，保留路径、URL 和组织策略的限制。"),
        "claude" or "codebuddy" or "qoder" => new("default", "auto", "使用 CLI 的自动审批策略，高风险操作仍可能被阻止或要求确认。"),
        "qwen" => new("default", "auto", "使用千问自动审批策略；仅支持旧模式的版本会自动接受编辑，其他操作仍需确认。"),
        "zcode" => new("build", "yolo", "YOLO 模式自动批准工具操作，包括写文件和执行命令。"),
        "cursor" or "opencode" => null,
        _ => throw new ArgumentOutOfRangeException(nameof(engine))
    };
}
internal sealed record EngineCapabilities(string ModeLabel, EngineMode[] Modes, string[] SessionActions,
    bool NativeArchive, bool Models = true, bool Efforts = true)
{
    public StartupApproval? StartupApproval { get; init; }
    internal bool Allows(string action) => Array.IndexOf(SessionActions, action) >= 0;
}

internal sealed record NativeSession(string Id, string? Model);
internal sealed record ModelChoice(string Id, string Name, bool IsDefault, string[] Efforts);
internal sealed record HistorySession(string Id, string Name, string Project, string? UpdatedAt)
{
    public bool MayBeInUse { get; init; }
    public bool NativeInUse { get; init; }
}
internal sealed record QuotaWindow(decimal UsedPercent, decimal RemainingPercent, int? WindowDurationMins, long? ResetsAt);
internal sealed record QuotaCredits(string? Balance, bool Unlimited);
internal sealed record EngineQuota(bool Supported, string? Message, QuotaWindow? Primary = null,
    QuotaWindow? Secondary = null, QuotaCredits? Credits = null, DateTimeOffset? CheckedAt = null, bool Unlimited = false)
{
    public string Status { get; init; } = Supported ? (Unlimited ? "unlimited" : "available") : "unavailable";
}
internal sealed record EngineMcp(string Root, string Verb, string Grant, string[] Tools, int? NotifyPort = null)
{
    internal IReadOnlyList<FF.AgentCollaboration.Domain.ManagedMcp> Managed { get; init; } = [];
    internal string? ExtraSkillRoot { get; init; }
    internal string Command => Environment.ProcessPath!;
    internal string[] Arguments
    {
        get
        {
            string[] args = Path.GetFileNameWithoutExtension(Command).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                ? [Assembly.GetExecutingAssembly().Location, Verb, Root] : [Verb, Root];
            return NotifyPort is int port ? [.. args, "--notify-port", port.ToString()] : args;
        }
    }
}

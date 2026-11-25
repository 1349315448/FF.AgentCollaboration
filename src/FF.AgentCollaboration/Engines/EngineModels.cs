using System.Reflection;

namespace FF.AgentCollaboration.Engines;

internal sealed record EngineMode(string Id, string Label);
internal sealed record EngineCapabilities(string ModeLabel, EngineMode[] Modes, string[] SessionActions,
    bool NativeArchive, bool Models = true, bool Efforts = true)
{
    internal bool Allows(string action) => Array.IndexOf(SessionActions, action) >= 0;
}

internal sealed record NativeSession(string Id, string? Model);
internal sealed record ModelChoice(string Id, string Name, bool IsDefault, string[] Efforts);
internal sealed record HistorySession(string Id, string Name, string Project, string? UpdatedAt)
{
    public bool MayBeInUse { get; init; }
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

namespace FF.AgentCollaboration.Domain;

internal sealed record FederationNode(string NodeId, string? NodeName, string? SelectedIp, string? PublicEndpoint);
internal sealed record NodeSettings(string? NodeName, string? SelectedIp, string? PublicEndpoint = null);
internal sealed record ShareSettings(bool Enabled, string PermissionMode = "read_only", bool ConfirmWrite = false,
    bool Rotate = false, bool? DedicatedReadOnly = null, string? RestartTerminal = null)
{
    // 旧客户端仅关闭共享时仍可立即撤权。显式发送 false 的工作台同时恢复本机权限。
    internal bool RestrictedTerminal(bool current) => Enabled ? DedicatedReadOnly == true : current && DedicatedReadOnly != false;

    internal void Validate()
    {
        if (!Enabled) return;
        if (DedicatedReadOnly == true && PermissionMode != "read_only") throw new WorkbenchError("受限终端只接受只读授权");
        FederationPolicy.Permission(PermissionMode, ConfirmWrite);
    }
}
internal sealed record PeerDecision(string Action, string PermissionMode = "read_only", bool ConfirmWrite = false,
    string QuotaMode = "daily", int? QuotaLimit = 20, bool Paused = false, bool AllowUpload = true, bool ResetTotal = false);
internal sealed record PeerApplication(string Claim, string Purpose);
internal sealed record PeerMessage(string Id, string Text, string? RequestId = null, string? UploadId = null, string? TopicId = null, bool ExpectsReply = true, bool? FinalReply = null, string? ContactKey = null, string? SenderKey = null)
{
    public string[]? AttachmentIds { get; init; }
    public string? AttachTo { get; init; }
}
internal sealed record RemoteApplication(string TopicId, string Link, string Purpose)
{
    private string? agentId;
    public string? AgentId { get => agentId; init { agentId = value; ContactSelected = true; } }
    internal bool ContactSelected { get; private set; }
}
internal sealed record ContactBinding(string TopicId, string? AgentId);
internal sealed record AdvertisedContact(string Key, string Name, bool Online, string? SenderKey = null);
internal sealed record ReturnContact(string Id, string WindowId, string Name, string NodeName, bool Online, bool CanMention, bool CanRemove, string GrantId);
internal sealed record PeerQuota(string Mode, int? Limit, long? Remaining, bool Paused, long? ResetsAt)
{
    internal static PeerQuota From(PeerGrant grant) =>
        new(grant.QuotaMode, grant.QuotaLimit, grant.QuotaLimit is int limit ? Math.Max(0L, limit - grant.Used) : null,
            grant.Paused, grant.ResetAt);
}
internal sealed record PeerSession(string Id, string Status, string NodeId, string NodeName, string AgentName, bool Online, string WindowId, string? Token = null, PeerQuota? Quota = null,string? GroupName=null,string? Engine=null);
internal sealed record PeerEvent(long Id, string Kind, string RequestId, string Text, string? DocumentId = null, string? FileName = null, bool? FinalReply = null, string? ChatTo = null);
internal sealed record PeerMail(long Seq, string TopicId, string Kind, string Source, string Target, string Label, string Text, string CreatedAt, bool HasDocument, string? WorkState, string? DeliveryState, int AttachmentCount);
internal sealed record PeerDispatch(string Id, string Remote, string Window, string Text, string? FileName, byte[]? Content, bool ExpectsReply, string? AttachTo);
internal sealed record OutgoingAttachment(string Id, string Name, byte[] Content);
internal sealed record PeerAttachmentApproval(string Id, string AgentId, string AgentName, string Recipient, string[] Files, string? Original);
internal sealed record PeerUpload(string UploadId, string PathHint);
internal sealed record PeerGrant(string Id, string WindowId, string NodeId, string NodeName, string Ip, string Purpose,
    string Status, string PermissionMode, string QuotaMode, int? QuotaLimit, bool Paused, bool AllowUpload, long Used,
    long? ResetAt, long? LastReceived, long Blocked, bool Connected);
internal sealed record RemoteCapsule(string Id, string TopicId, string NodeId, string NodeName, string AgentName,
    string Status, bool Online, bool Connected, string? LastReply, string? Error, PeerQuota? Quota, string? ContactAgentId, string[] DeniedAgentIds, bool TeamMember,string? GroupName,string? Engine);
internal sealed record RemoteConnection(string Id, string WindowId, string Endpoint, string Pin, string Provider, string? Share,
    string Claim, string? Token, string Status, string Purpose, long Cursor, bool Detached);
internal sealed record FederationSummary(IReadOnlyList<PeerGrant> Grants, IReadOnlyList<RemoteCapsule> Remotes,
    IReadOnlyList<string> SharedWindows, IReadOnlyList<PeerMail> Mail, IReadOnlyList<ReturnContact> Contacts,
    IReadOnlyList<string> RequestedReviews, IReadOnlyList<PeerAttachmentApproval> AttachmentApprovals, IReadOnlyList<GroupView> Groups);

internal sealed class FederationException(int status, string code, object? details = null) : Exception(code)
{
    internal int Status { get; } = status;
    internal object Payload => details ?? new { error = Message };
}

internal static class FederationPolicy
{
    internal static string DefaultName()
    {
        string value = new(Environment.MachineName.Where(c => !char.IsControl(c)).Take(60).ToArray());
        return string.IsNullOrWhiteSpace(value) ? "LocalNode" : value.Trim();
    }

    internal static string Name(string? name)
    {
        string value = WorkbenchInput.Text(name, "节点名称", 60);
        if (value.Any(char.IsControl)) throw new WorkbenchError("节点名称包含控制字符");
        return value;
    }
    internal static void Permission(string mode, bool confirm)
    {
        if (mode != "read_only" && !(mode == "read_write" && confirm))
            throw new WorkbenchError("允许修改需要宿主再次确认");
    }
    internal static bool IsSecret(string? text) => text is { Length: 64 } && text.All(char.IsAsciiHexDigit);
    internal static (long Start, long? End) Period(string mode, long now)
    {
        long length = mode switch { "daily" => 86400, "hourly" => 3600, _ => 0 };
        long start = length == 0 ? 0 : now - now % length;
        return (start, length == 0 ? null : start + length);
    }
}

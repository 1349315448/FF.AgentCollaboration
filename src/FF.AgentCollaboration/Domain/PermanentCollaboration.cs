namespace FF.AgentCollaboration.Domain;

internal sealed record GroupMember(string Id, string Name, string Engine, bool Online);
internal sealed record GroupRoster(string Topic, string Name, string Leader, GroupMember[] Members, bool Active);
internal sealed record GroupSelection(string[] Members, PeerDecision Policy, string? MemberMode = null, string? SuggestedMember = null, long? ExpiresAt = null);
internal sealed record GroupApplication(string Id, string Claim, string Purpose, GroupRoster Roster, string? SuggestedMember = null, bool FullGroup = false);
internal sealed record GroupSession(string Id, string Status, string Node, string User, GroupRoster Roster, PeerQuota? Quota,
    bool AllowUpload, string? Token = null);
internal sealed record GroupView(string Id, string Topic, string Status, bool Requester, bool Connected, string User,
    GroupRoster Remote, string[] Members, PeerGrant Policy, PeerQuota? RemoteQuota, bool RemoteAllowsUpload, string Purpose,
    GroupRouteMember[] Recipients, string MemberMode, string? SuggestedMember, long? ExpiresAt, bool NeedsInvitation);
internal sealed record GroupRouteMember(string Key, string Link, string Window, string Name, string Topic, string User,
    string? Engine, bool Leader, bool Online);
internal sealed record GroupFileRef(string Id, string Name);
internal sealed record GroupFileRoute(string Message,string? Sender,string? Recipient);
internal sealed record GroupPacket(string Id, string? Sender, string? Recipient, string Text, string? ReplyTo,
    bool ExpectsReply, bool? FinalReply, GroupFileRef[] Files);
internal sealed record GroupReceipt(string Id, string Delivery, string? Work);
internal sealed record GroupExchange(GroupRoster? Roster, PeerQuota? Quota, bool AllowUpload, bool Revoke,
    GroupPacket? Message, string[] Track, GroupReceipt[] Receipts);
internal sealed record GroupExchangeResult(GroupSession Session, GroupPacket? Message, string[] Track, GroupReceipt[] Receipts);

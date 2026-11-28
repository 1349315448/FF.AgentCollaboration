namespace FF.AgentCollaboration.Domain;

internal sealed record ChatInput(string? Text, string[]? Targets, string[]? Files, bool RequestReply = false, string? HumanPeer = null);
internal sealed record TeamRoleEdit(string AgentId, string? Role);
internal sealed record ChatLink(string Id,string Name,string? LeaderId,string? LeaderName);
internal sealed record ChatLinkInput(string Group);
internal sealed record ChatLinkSelection(string Group,bool Enabled,string[]? Members,string[]? PeerMembers,string? MemberMode=null,string? PeerMode=null);
internal sealed record GroupPair(string First,string Second,string[] FirstMembers,string[] SecondMembers,string FirstMode,string SecondMode);
internal sealed record ChatReceipt(string Id, string Target, string Name, string Delivery, string? Work, bool Acknowledged);
internal sealed record ChatAttachment(string Name, string Key, string? Project = null, string? Path = null);
internal sealed record ChatPost(string Id, string Topic, string Source, string Sender, string Text, string CreatedAt,
    string? ReplyTo, ChatAttachment[] Attachments, ChatReceipt[] Receipts, long? PeerSeq = null, string? Kind = null,
    string? Application = null, string? ReplyTopic = null);
internal sealed record ChatUpload(string Name, Stream Content);

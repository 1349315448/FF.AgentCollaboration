namespace FF.AgentCollaboration.Domain;

internal sealed record TeamProposalMember(string Name, string Role, string TaskSummary,
    string? SuggestedEngine = null, string? SuggestedModel = null, string? SuggestedEffort = null, string? RemoteId = null);

internal sealed record TeamProposalInput(string? Summary, TeamProposalMember?[]? Members);

internal sealed record TeamProposal(string Id, string TopicId, string LeaderWindowId, string LeaderName,
    string Summary, TeamProposalMember[] Members, string Status, string CreatedAt);

internal sealed record TeamMemberSelection(int Index, string? Name, string? Role, string? Engine,
    string? Model = null, string? Effort = null, string? Mode = null, string? Approval = null, string? RemoteId = null);

internal sealed record TeamConfirmation(TeamMemberSelection[]? Members);

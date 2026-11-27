namespace FF.AgentCollaboration.Domain;

internal sealed record TeamCreatedMember(int Index, string Name, string? WindowId, bool Online, string? Error = null, string? RemoteId = null);
internal sealed record ExternalTeamMember(string Id, string Name, string NodeName, string Role, string TaskSummary, bool Online, bool Authorized);
internal sealed record TeamCreationResult(string Status, IReadOnlyList<TeamCreatedMember> Members, string? RosterWarning = null);

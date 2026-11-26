namespace FF.AgentCollaboration.Application;

internal sealed record CreateAgent(string? Name, string? Project, string? Engine, string? Model,
    string? Session, string? Effort, string? Mode, bool NewSession, string? Topic, bool ForkSession = false, string? Approval = null, string? Role = null);
internal sealed record TerminalInput(string? Text, string? MessageId);
internal sealed record AgentConfiguration(string? Name, string? Role, string? Engine, string? SessionMode, string? SessionId,
    string? Model, string? Effort, string? Mode, string? Approval);
internal sealed record ChangeSession(bool NewSession, string? Session);
internal sealed record HistoryAction(string? Session, bool Archived, string? Engine = null);
internal sealed record ChangeAutoReceive(bool? Enabled, string? Mode);
internal sealed record OpenApproval(string Terminal);
internal sealed record ChangeApproval(string? Terminal, string Approval);
internal sealed record RenameAgent(string? Name);
internal sealed record SelectProject(string? Path);

internal sealed record MentionTarget(string SourceTerminal, string TargetId, string TargetTerminal);

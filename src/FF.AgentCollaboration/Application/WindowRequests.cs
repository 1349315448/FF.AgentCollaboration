namespace FF.AgentCollaboration.Application;

internal sealed record CreateAgent(string? Name, string? Project, string? Engine, string? Model,
    string? Session, string? Effort, string? Mode, bool NewSession, string? Topic, bool ForkSession = false);
internal sealed record TerminalInput(string? Text, string? MessageId);
internal sealed record ChangeSession(bool NewSession, string? Session);
internal sealed record HistoryAction(string? Session, bool Archived);
internal sealed record ChangeAutoReceive(bool Enabled);
internal sealed record RenameAgent(string? Name);
internal sealed record SelectProject(string? Path);

internal sealed record MentionTarget(string SourceTerminal, string TargetId, string TargetTerminal);

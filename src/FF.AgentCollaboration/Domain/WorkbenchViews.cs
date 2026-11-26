namespace FF.AgentCollaboration.Domain;

internal sealed record WindowView(AgentWindow Agent, string? Terminal, uint? ExitCode, bool InputReady, string Activity, IReadOnlyList<InboxItem> Inbox, string? WakeError = null, bool McpRestartRequired = false);
internal sealed record TimelineEntry(string Id, string Source, string Target, string From, string To, string Summary, string State, string? CreatedAt, bool Acknowledged, bool HasDocument, string? WorkState);
internal sealed record HandoffRoute(string Id, string Source, string Target, string State, bool Acknowledged);
internal sealed record WorkbenchView(string? Project, IReadOnlyList<string> Projects, IReadOnlyList<WorkbenchTopic> Topics, IReadOnlyList<WindowView> Windows,
    string? TimelineTopic, IReadOnlyList<TimelineEntry> Timeline, string MentionShortcut, IReadOnlyList<HandoffRoute> RecentHandoffs,
    FederationSummary Federation);

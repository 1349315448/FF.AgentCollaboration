namespace FF.AgentCollaboration.Domain;

internal sealed record WorkbenchTopic(string Id, string Name, string Project, string? Goal, string Status,
    string? Folder, string CreatedAt, string? FinishedAt, string? FinishSummary)
{
    public string? Activity { get; init; }
}
internal sealed record TopicInput(string? Name, string? Project, string? Goal, string? Folder);

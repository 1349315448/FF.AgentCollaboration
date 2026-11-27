namespace FF.AgentCollaboration.Domain;

internal sealed record AgentWindow(string Id, string Name, string Engine, string Project, string? Model,
    string? Session, string? Effort, string? Mode, bool AutoReceive, bool Online, string TopicId)
{
    public string? Role { get; init; }
    public bool IsLeader { get; init; }
    internal bool NewSessionPending { get; init; }
    public string? SessionTitle { get; init; }
    public string? Approval { get; init; }
    public string ReceiveMode { get; init; } = "queued";
}

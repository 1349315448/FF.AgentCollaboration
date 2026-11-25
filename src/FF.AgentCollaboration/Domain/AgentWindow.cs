namespace FF.AgentCollaboration.Domain;

internal sealed record AgentWindow(string Id, string Name, string Engine, string Project, string? Model,
    string? Session, string? Effort, string? Mode, bool AutoReceive, bool Online, string TopicId)
{
    public string? SessionTitle { get; init; }
}

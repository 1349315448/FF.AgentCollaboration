namespace FF.AgentCollaboration.Domain;

internal static class CollaborationTools
{
    internal const string ListWindows = "list_windows";
    internal const string Send = "send_to_agent";
    internal const string ReadDocument = "read_handoff_doc";
    internal const string FinishTopic = "finish_topic";
    internal static readonly string[] Names = [ListWindows, Send, ReadDocument, FinishTopic];
}

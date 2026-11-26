namespace FF.AgentCollaboration.Domain;

internal static class CollaborationTools
{
    internal const string ListWindows = "list_windows";
    internal const string Send = "send_to_agent";
    internal const string Receive = "receive_message";
    internal const string ReportTask = "report_task_status";
    internal const string ReadDocument = "read_handoff_doc";
    internal const string FinishTopic = "finish_topic";
    internal const string ReadSkill = "read_collaboration_skill";
    internal static readonly string[] Names = [ListWindows, Send, Receive, ReportTask, ReadDocument, FinishTopic, ReadSkill];
}

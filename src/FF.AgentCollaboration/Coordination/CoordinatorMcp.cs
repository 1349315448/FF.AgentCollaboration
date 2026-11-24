using FF.AgentCollaboration.Presentation.McpServer;

namespace FF.AgentCollaboration.Coordination;

internal static class CoordinatorMcp
{
    internal static readonly string[] P0ToolNames = ["handoff_task", "read_handoff", "report_task_done", "verify_result"];
    private static readonly object[] P0Tools = [
        Mcp.Tool("handoff_task","Dispatch a task. Put the requested handoff document in text.", "taskId", "text"),
        Mcp.Tool("read_handoff","Read the document assigned to this window.", "taskId"),
        Mcp.Tool("report_task_done","Save a completed result in text and wake the original session.", "taskId", "text"),
        Mcp.Tool("verify_result","Save the original session's final verification in text.", "taskId", "text")
    ];

    internal static async Task Serve(string root)
    {
        var grant = Mcp.WindowGrant();
        using var state = new State(root);
        state.Caller(grant);
        await Mcp.Serve(P0Tools, (name, input) => state.Call(grant, name, (string)input["taskId"]!, (string?)input["text"]));
    }

}

using System.Text.Json;
using System.Text.Json.Nodes;
using FF.AgentCollaboration.Application;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Storage;

namespace FF.AgentCollaboration.Presentation.McpServer;

internal static class WorkbenchMcp
{
    private static readonly object[] Tools =
    [
        new
        {
            name = CollaborationTools.ListWindows, description = "List peers in this topic. allTopics includes other topics and their @topic/name addresses. detail adds ID, engine and project.",
            inputSchema = new { type = "object", properties = new { detail = new { type = "boolean" }, allTopics = new { type = "boolean" } }, additionalProperties = false }
        },
        new
        {
            name = CollaborationTools.Send,
            description = "Send target + message. Share Markdown from your project or topic archive with document + a summary (up to 500 characters). To resend your own message unchanged, pass resend and optionally a new target.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    target = new { type = "string", description = "@name in this topic, @topic/name, or window ID" },
                    message = new { type = "string" },
                    document = new { type = "string", description = "Existing .md path in your project or your topic archive, shared in place." },
                    resend = new { type = "string", description = "Original message ID. Omit message and document." },
                    files = new { type = "array", items = new { type = "string" }, description = "Existing files within your project directory. Relative or absolute paths." },
                    autoReturnHint = new { type = new[] { "string", "boolean" }, description = "Optional instruction to return results. True asks the recipient to reply using send_to_agent." }
                },
                additionalProperties = false
            }
        },
        Mcp.Tool(CollaborationTools.ReadDocument, "Read a Markdown handoff that you sent or received. Use the delivered absolute path or its message ID.", "pathOrId"),
        Mcp.Tool(CollaborationTools.FinishTopic, "Finish your current topic with a short completion summary.", "summary")
    ];

    internal static Task Serve(string root)
    {
        string grant = Mcp.WindowGrant();
        return Mcp.Serve(Tools, (name, input) => Call(root, grant, name, input));
    }

    private static string Call(string root, string grant, string name, JsonNode input)
    {
        using var state = new WorkbenchStore(root);
        switch (name)
        {
            case CollaborationTools.FinishTopic:
                var finished = state.FinishOwnTopic(grant, WorkbenchInput.Text((string?)input["summary"], "完成摘要", 2000));
                return JsonSerializer.Serialize(new { id = finished.Id, status = finished.Status, summary = finished.FinishSummary });
            case CollaborationTools.ListWindows:
                var caller = state.Caller(grant);
                bool all = (bool?)input["allTopics"] == true;
                var topics = state.Topics().ToDictionary(t => t.Id, t => t.Name);
                var peers = state.Windows().Where(a => a.Id != caller.Id && (all || a.TopicId == caller.TopicId));
                return JsonSerializer.Serialize(peers.Select(a =>
                {
                    var item = new Dictionary<string, object>
                    {
                        ["name"] = "@" + (a.TopicId == caller.TopicId ? "" : topics[a.TopicId] + "/") + a.Name,
                        ["online"] = a.Online
                    };
                    if (all) item["topic"] = topics[a.TopicId];
                    if ((bool?)input["detail"] == true)
                    {
                        item["id"] = a.Id;
                        item["engine"] = a.Engine;
                        item["project"] = a.Project;
                    }
                    return item;
                }));
            case CollaborationTools.Send:
                var hint = input["autoReturnHint"] is JsonValue value && value.TryGetValue<bool>(out var reply)
                    ? reply.ToString() : (string?)input["autoReturnHint"];
                return Queued(HandoffService.Send(state, grant, (string?)input["target"], (string?)input["message"],
                    (string?)input["document"], (string?)input["resend"], input["files"]?.Deserialize<string[]>() ?? [], hint));
            case CollaborationTools.ReadDocument:
                return state.ReadDocument(grant, WorkbenchInput.Text((string?)input["pathOrId"], "交接文档", 4096));
            default:
                throw new WorkbenchError("未知协作工具");
        }
    }

    private static string Queued(InboxItem delivery) =>
        delivery.QueueHint == null ? JsonSerializer.Serialize(new { id = delivery.Id, status = "queued", document = delivery.Document })
        : JsonSerializer.Serialize(new { id = delivery.Id, status = "queued", document = delivery.Document, hint = delivery.QueueHint });
}

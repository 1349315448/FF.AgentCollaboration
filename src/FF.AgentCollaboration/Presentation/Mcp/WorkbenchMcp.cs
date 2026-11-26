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
                    replyTo = new { type = "string", description = "Original MSG_ID when replying to a task. A linked reply is final unless finalReply is false." },
                    finalReply = new { type = "boolean", description = "With replyTo, set false for an interim acknowledgment and true for final results." },
                    resend = new { type = "string", description = "Original message ID. Omit message and document." },
                    supersedePrevious = new { type = "boolean", @default = true, description = "Replace your earlier pending or uncertain messages to this recipient. Set false for independent tasks." },
                    cancel = new { type = new[] { "string", "boolean" }, description = "Only retract your own unclaimed or uncertain message: its full message ID alone, or true with target to retract the latest eligible message. Does not stop work already started." },
                    files = new { type = "array", items = new { type = "string" }, description = "Existing files within your project directory. Relative or absolute paths." },
                    autoReturnHint = new { type = new[] { "string", "boolean" }, description = "Optional instruction to return results. True asks the recipient to reply using send_to_agent." }
                },
                additionalProperties = false
            }
        },
        new { name=CollaborationTools.ReportTask, description="Report starting or completing the specific task received by this window. If a reply was requested send the final result with send_to_agent and replyTo instead of completed.",
            inputSchema=new { type="object", properties=new { messageId=new {type="string"}, status=new {type="string", @enum=new[]{"working","completed"}} },
                required=new[]{"messageId","status"}, additionalProperties=false } },
        Mcp.Tool(CollaborationTools.ReadDocument, "Read a Markdown handoff you sent or received. Use its HANDOFF_DOC path, full message ID, or unique 8-hex MSG_ID. Plain MSG messages have no handoff document.", "pathOrId"),
        Mcp.Tool(CollaborationTools.FinishTopic, "Finish your current topic with a short completion summary.", "summary"),
        Mcp.Tool(CollaborationTools.ReadSkill, "Read this FF.CP window's ff-agent-collab skill and examples for delegation, review, and delivering images to the user.")
    ];

    private static readonly HttpClient Notifications = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
        { Timeout = TimeSpan.FromMilliseconds(500) };

    internal static Task Serve(string root, int? port)
    {
        string grant = Mcp.WindowGrant();
        (string Name, string? Role)? known = null;
        string? updatedIdentity = null;
        void ObserveIdentity(AgentWindow window)
        {
            var identity = (window.Name, window.Role);
            if (known.HasValue && known.Value != identity)
                updatedIdentity = $"当前窗口名称：@{window.Name}。"+(window.Role == null ? "当前未设置身份。" : $"当前窗口身份：{window.Role}。");
            known = identity;
        }
        return Mcp.Serve(Tools, (name, input) =>
        {
            string result = Call(root, grant, name, input, ObserveIdentity);
            if (port.HasValue && name is CollaborationTools.Send or CollaborationTools.FinishTopic or CollaborationTools.ReportTask)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/events/wake");
                request.Headers.Add("X-FF-Notify", "1");
                try { using var response = Notifications.Send(request); }
                catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
                { /* 已提交的操作仍成功，宿主定期核对 SQLite。 */ }
            }
            return result;
        }, () =>
        {
            using var state = new WorkbenchStore(root);
            var caller = state.Caller(grant);
            known = (caller.Name, caller.Role);
            updatedIdentity = null;
            return $"FF.Agent 窗口 @{caller.Name}，所属话题「{state.Topic(caller.TopicId).Name}」。" +
                (caller.Role == null ? "" : $"当前窗口身份：{caller.Role}。") +
                "用 @名称 或 @话题/名称 寻址。长文通过 document 传 Markdown 路径，message 用简短摘要。" +
                "开始收到的任务用 report_task_status(MSG_ID, working)；无须回信用 completed 结案，需回信用 send_to_agent 的 replyTo 关联原 MSG_ID。中途回信加 finalReply=false。更新需求默认 supersedePrevious=true；独立任务设为 false。交接、审核或交付图片时，调用 read_collaboration_skill 读取本窗口的 ff-agent-collab 规范和示例。";
        }, () => { var update = updatedIdentity; updatedIdentity = null; return update; });
    }

    private static string Call(string root, string grant, string name, JsonNode input, Action<AgentWindow> observeIdentity)
    {
        using var state = new WorkbenchStore(root);
        var caller = state.Caller(grant);
        observeIdentity(caller);
        if (name==CollaborationTools.FinishTopic || name==CollaborationTools.Send && (input["cancel"]!=null || input["resend"]!=null))
            if(state.DedicatedReader(caller.Id)) throw new WorkbenchError("专用审阅会话不支持该操作",WorkbenchFailure.Forbidden);
        switch (name)
        {
            case CollaborationTools.ReportTask:
                var status=state.ReportTask(grant,WorkbenchInput.Text((string?)input["messageId"],"MSG_ID",32),WorkbenchInput.Text((string?)input["status"],"协作状态",16));
                return JsonSerializer.Serialize(new {status});
            case CollaborationTools.ReadSkill:
                return CollaborationSkill.Read();
            case CollaborationTools.FinishTopic:
                var finished = state.FinishOwnTopic(grant, WorkbenchInput.Text((string?)input["summary"], "完成摘要", 2000));
                return JsonSerializer.Serialize(new { id = finished.Id, status = finished.Status, summary = finished.FinishSummary });
            case CollaborationTools.ListWindows:
                if(state.DedicatedReader(caller.Id))
                    return state.CurrentPeerRequest(caller.Id) is string route
                        ? JsonSerializer.Serialize(new[]{new {name="@reply-"+route,online=true}}) : "[]";
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
                    if (a.Role != null) item["role"] = a.Role;
                    if (all) item["topic"] = topics[a.TopicId];
                    if ((bool?)input["detail"] == true)
                    {
                        item["id"] = a.Id;
                        item["engine"] = a.Engine;
                        item["project"] = a.Project;
                    }
                    return item;
                }).Concat(state.RemoteRecipients(caller.TopicId).Select(r =>
                {
                    var item = new Dictionary<string,object> { ["name"]="@"+r.AgentName, ["online"]=r.Online, ["id"]=r.Id, ["node"]=r.NodeName };
                    if ((bool?)input["detail"]==true) item["remote"]=true;
                    return item;
                })));
            case CollaborationTools.Send:
                if (input.AsObject().ContainsKey("cancel"))
                {
                    if (input.AsObject().Any(p => p.Key is not ("cancel" or "target")))
                        throw new WorkbenchError("撤回时请只传 cancel，按目标撤回时另传 target");
                    string cancelled;
                    if (input["cancel"] is JsonValue option && option.TryGetValue<bool>(out var byTarget) && byTarget)
                        cancelled = state.Cancel(grant, null, WorkbenchInput.Text((string?)input["target"], "收件 Agent", 160));
                    else if (input["cancel"] is JsonValue token && token.TryGetValue<string>(out var id) && input["target"] == null)
                        cancelled = state.Cancel(grant, WorkbenchInput.Id(id), null);
                    else throw new WorkbenchError("请用 cancel=消息 ID 或 cancel=true 加 target 撤回");
                    return JsonSerializer.Serialize(new { id = cancelled, status = "cancelled" });
                }
                var hint = input["autoReturnHint"] is JsonValue value && value.TryGetValue<bool>(out var reply)
                    ? reply.ToString() : (string?)input["autoReturnHint"];
                return Queued(HandoffService.Send(state, grant, (string?)input["target"], (string?)input["message"],
                    (string?)input["document"], (string?)input["resend"], input["files"]?.Deserialize<string[]>() ?? [], hint,
                    (bool?)input["supersedePrevious"] ?? true,(string?)input["replyTo"],(bool?)input["finalReply"]));
            case CollaborationTools.ReadDocument:
                if(state.DedicatedReader(caller.Id))
                    return state.ReadCurrentPeerDocument(grant,WorkbenchInput.Text((string?)input["pathOrId"],"交接文档",4096));
                return state.ReadDocument(grant, WorkbenchInput.Text((string?)input["pathOrId"], "交接文档", 4096));
            default:
                throw new WorkbenchError("未知协作工具");
        }
    }

    private static string Queued(InboxItem delivery) =>
        delivery.QueueHint == null ? JsonSerializer.Serialize(new { id = delivery.Id, status = "queued", document = delivery.Document })
        : JsonSerializer.Serialize(new { id = delivery.Id, status = "queued", document = delivery.Document, hint = delivery.QueueHint });
}

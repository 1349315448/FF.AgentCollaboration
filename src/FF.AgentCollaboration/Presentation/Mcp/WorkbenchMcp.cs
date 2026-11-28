using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
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
                    target = new { type = "string", description = "@name, @topic/name, window ID, or @全部 with broadcast=true for the current topic leader." },
                    targets = new { type = "array", items = new { type = "string" }, minItems = 1, maxItems = 50,
                        description = "Send one group message to selected recipients with independent receipts. Use this in place of target." },
                    broadcast = new { type = "boolean", description = "Set true only when the user explicitly requests notifying the whole current topic. Use target=@全部." },
                    externalTargets = new { type = "array", items = new { type = "string" }, description = "With @全部 only. Include the specified authorized external team members only when the user explicitly chooses them. Use IDs from kb_team_context." },
                    message = new { type = "string" },
                    document = new { type = "string", description = "Existing .md path in your project or your topic archive, shared in place." },
                    replyTo = new { type = "string", description = "Original MSG_ID when replying to a task. A linked reply is final unless finalReply is false." },
                    finalReply = new { type = "boolean", description = "With replyTo, set false for an interim acknowledgment and true for final results." },
                    resend = new { type = "string", description = "Original message ID. Omit message and document." },
                    supersedePrevious = new { type = "boolean", @default = true, description = "Replace your earlier pending or uncertain messages to this recipient. Set false for independent tasks." },
                    cancel = new { type = new[] { "string", "boolean" }, description = "Only retract your own unclaimed or uncertain message: its full message ID alone, or true with target to retract the latest eligible message. Does not stop work already started." },
                    files = new { type = "array", items = new { type = "string" }, description = "Existing project files, up to 20 per message. Deliver user-facing artifacts to @工作台用户. Include replyTo for a group chat request; omit it for an original CLI request." },
                    autoReturnHint = new { type = new[] { "string", "boolean" }, description = "Optional instruction to return results. True asks the recipient to reply using send_to_agent." }
                },
                additionalProperties = false
            }
        },
        new { name=CollaborationTools.ReportTask, description="Report starting work, waiting for clarification, or completing a received task. If a reply was requested send the final result with send_to_agent and replyTo instead of completed.",
            inputSchema=new { type="object", properties=new { messageId=new {type="string"}, status=new {type="string", @enum=new[]{"working","need_info","completed"}} },
                required=new[]{"messageId","status"}, additionalProperties=false } },
        new { name=CollaborationTools.ResumeAfterRestart, description="Save a task for this Agent's current session. The user must select it for an update or restart. It is delivered only after the new host confirms that operation. Calling again replaces this Agent's unassigned draft.",
            inputSchema=new {type="object",properties=new {message=new {type="string",maxLength=4000}},
                required=new[]{"message"},additionalProperties=false} },
        new { name=CollaborationTools.WorkbenchUpdate,description="Check the configured signed workbench release channel or request a local update/restart. Apply and restart require the user's per-Agent permission. Save your own continuation first with resume_after_restart. Use mode idle to wait for screens to be safe.",
            inputSchema=new {type="object",properties=new {action=new {type="string",@enum=new[]{"check","apply","restart"}},
                version=new {type="string"},mode=new {type="string",@enum=new[]{"idle","now"}}},required=new[]{"action"},additionalProperties=false} },
        Mcp.Tool(CollaborationTools.ShowExternalApproval, "Show the human approval dialog for an external collaboration application addressed to this Agent. Use only the application ID from the workbench notice. This tool cannot approve or reject.", "applicationId"),
        new { name=CollaborationTools.SendPeerFiles,
            description="Send files to an authorized external Agent. For a new message use target + message; use attachTo instead to supplement an original message sent by this Agent. Only relative project paths. The workbench asks the user unless this Agent already has permanent approval.",
            inputSchema=new { type="object", properties=new {target=new {type="string"}, message=new {type="string"},
                attachTo=new {type="string"}, files=new {type="array", items=new {type="string"}, minItems=1, maxItems=20}},
                required=new[]{"files"}, additionalProperties=false } },
        Mcp.Tool(CollaborationTools.ReadDocument, "Read a Markdown handoff you sent or received. Use its HANDOFF_DOC path, full message ID, or unique 8-hex MSG_ID. Plain MSG messages have no handoff document.", "pathOrId"),
        new
        {
            name = CollaborationTools.Receive,
            description = "Receive one released message in this conversation. Pass the full messageId from a channel notification or omit it for the oldest available message. Null means nothing to process.",
            inputSchema = new { type = "object", properties = new { messageId = new {type="string"},
                channelReceipt = new {type="string",description="Copy the receipt from the native channel notification when present."} }, additionalProperties = false }
        },
        Mcp.Tool(CollaborationTools.FinishTopic, "Finish your current topic with a short completion summary.", "summary"),
        new { name = CollaborationTools.UpdateTeam,
            description = "Current leader only. Update this topic's existing goal and optional role prompts of members. Does not restart terminals.",
            inputSchema = new { type = "object", properties = new {
                goal = new { type = new[] { "string", "null" }, maxLength = 2000 },
                roles = new { type = "array", maxItems = 50, items = new { type = "object", properties = new {
                    agentId = new { type = "string" }, role = new { type = new[] { "string", "null" }, maxLength = 200 }
                }, required = new[] { "agentId", "role" }, additionalProperties = false } }
            }, additionalProperties = false } },
        Mcp.Tool(CollaborationTools.ReadSkill, "Read workbench UI instructions for answering user questions, plus collaboration tools and examples for this FF.CP window."),
        Mcp.Tool(CollaborationTools.ReadKnowledgeSkill, "Read the ff-acp-knowledge skill for finding and preserving knowledge and requesting clarification."),
        Mcp.Tool(CollaborationTools.ReadTeamSkill, "Read this window's current leader or member guide for team assignments, shared and personal records, and leadership handover."),
        Mcp.Tool(CollaborationTools.KnowledgeTeam, "Get this topic's leader, roster, shared plan, query for member task records, and your own memory summary."),
        Mcp.Tool(CollaborationTools.KnowledgeRead, "Read a knowledge Markdown document by its relative path from kb_search or kb_team_context.", "path"),
        Mcp.Tool(CollaborationTools.AvailableEngines, "List installed local engines and cached account, model and quota status for recommending team members."),
        new
        {
            name = CollaborationTools.ProposeTeam,
            description = "For the current topic leader only. Discuss roles and suggested identity prompts with the user first, then submit 1 to 8 suggested members for their confirmation. This tool creates a proposal only.",
            inputSchema = new { type = "object", properties = new {
                summary = new { type = "string", maxLength = 2000 },
                members = new { type = "array", minItems = 1, maxItems = 8, items = new {
                    type = "object", properties = new {
                        name = new { type = "string", maxLength = 40 }, role = new { type = "string", maxLength = 200 },
                        taskSummary = new { type = "string", maxLength = 500 },
                        remoteId = new { type = "string", description = "Invite an existing authorized remote capsule in this topic. Use its exact id from list_windows(detail=true)." },
                        suggestedEngine = new { type = "string", @enum = EngineNames.All }, suggestedModel = new { type = "string", maxLength = 200 },
                        suggestedEffort = new { type = "string", maxLength = 200 }
                    }, required = new[] { "name", "role", "taskSummary" }, additionalProperties = false
                } }
            }, required = new[] { "summary", "members" }, additionalProperties = false }
        },
        new
        {
            name = CollaborationTools.KnowledgeSearch,
            description = "Search the knowledge library for your project, current topic, and your own Agent memory.",
            inputSchema = new { type = "object", properties = new {
                query = new { type = "string" }, scope = new { type = "string", @enum = new[] {"project", "topic", "agent"} },
                theme = new { type = "string" }, keywords = new { type = "array", items = new { type = "string" } },
                topicId = new { type = "string" }, limit = new { type = "integer", minimum = 1, maximum = 100 },
                recordsOnly = new { type = "boolean", description = "List shared task records within the current topic." },
                offset = new { type = "integer", minimum = 0 }
            }, additionalProperties = false }
        },
        new
        {
            name = CollaborationTools.KnowledgeWrite,
            description = "Write Markdown in an authorized knowledge scope. Use a path relative to the selected scope or a knowledge-relative path returned by a tool.",
            inputSchema = new { type = "object", properties = new {
                scope = new { type = "string", @enum = new[] {"project", "topic", "agent"} }, path = new { type = "string" },
                content = new { type = "string" }, title = new { type = "string" }, theme = new { type = "string" },
                keywords = new { type = "array", items = new { type = "string" } },
                status = new { type = "string", @enum = new[] {"planned","working","done","blocked"} },
                expectedVersion = new { type = "string", description = "Required for existing topic PLAN.md, TEAM.md and records/ documents. Use the version from kb_read." }
            }, required = new[] { "scope", "path", "content" }, additionalProperties = false }
        }
    ];
    private static readonly JsonSerializerOptions KnowledgeJson = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions TeamInputJson = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private const string LeaderInstruction = "你是本话题组长。";

    private static readonly HttpClient Notifications = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
        { Timeout = TimeSpan.FromMilliseconds(500) };
    private static readonly HttpClient Updates = new(new SocketsHttpHandler { AllowAutoRedirect=false, UseProxy=false })
        { Timeout=TimeSpan.FromSeconds(12) };

    internal static Task Serve(string root, int? port)
    {
        string grant = Mcp.WindowGrant();
        (string Name, string? Role, bool Leader)? known = null;
        string? updatedIdentity = null;
        string? receipt = null, lastHint = null;
        bool supportsChannel = false;
        void ObserveIdentity(AgentWindow window)
        {
            var identity = (window.Name, window.Role, window.IsLeader);
            if (known.HasValue && known.Value != identity)
            {
                updatedIdentity = $"当前窗口名称：@{window.Name}。" + (window.Role == null ? "当前未设置身份。" : $"当前窗口身份：{window.Role}。") +
                    (window.IsLeader ? LeaderInstruction : known.Value.Leader ? "你已卸任本话题组长。" : "");
                if (known.Value.Leader != window.IsLeader)
                    updatedIdentity += window.IsLeader ? "调用 read_team_skill，按接任流程核对计划和在途任务。" :
                        "调用 read_team_skill，按卸任流程交接未结事项及旧任务回信。";
            }
            known = identity;
        }
        return Mcp.Serve(Tools, (name, input) =>
        {
            if (name == CollaborationTools.Receive)
            {
                using var state = new WorkbenchStore(root);
                ObserveIdentity(McpCaller(state, grant));
                string? id = (string?)input["messageId"];
                string? channelReceipt = (string?)input["channelReceipt"];
                bool channelRead = id != null && channelReceipt == ChannelReceipt(grant,id);
                var message = state.TakeNativeMessage(grant,id == null ? null : WorkbenchInput.Id(id),channelRead);
                receipt = message?.Id;
                return message == null ? "null" : JsonSerializer.Serialize(new {id=message.Id,message=message.AsText()});
            }
            string result = Call(root, grant, name, input, ObserveIdentity, port);
            if (port.HasValue && name is CollaborationTools.Send or CollaborationTools.FinishTopic or CollaborationTools.ReportTask or
                CollaborationTools.ProposeTeam or CollaborationTools.ShowExternalApproval or CollaborationTools.SendPeerFiles or CollaborationTools.UpdateTeam or CollaborationTools.ResumeAfterRestart)
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
            var caller = McpCaller(state, grant);
            supportsChannel = caller.Engine is "claude" or "codebuddy";
            known = (caller.Name, caller.Role, caller.IsLeader);
            updatedIdentity = null;
            return $"FF.Agent 窗口 @{caller.Name}，所属话题「{state.Topic(caller.TopicId).Name}」。" +
                (caller.Role == null ? "" : $"当前窗口身份：{caller.Role}。") +
                (caller.IsLeader ? LeaderInstruction : "") +
                (state.DedicatedReader(caller.Id) ? "当前是专用审阅会话。按来信授权审阅并回信，通过回信交付记录。" : "") +
                "首次处理本话题团队分工或交接时，调用 read_team_skill 读取当前职责手册。" +
                "需要查询、记录项目知识或索取澄清时，按需调用 read_knowledge_skill。" +
                "协作收件通过 receive_message 进入当前会话。收到信件编号时调用该工具领取，空结果表示已由其他调用领取或已撤回。" +
                "用 @名称 或 @话题/名称 寻址。长文通过 document 传 Markdown 路径，message 用简短摘要。" +
                "向本机用户交付图片、视频、文档、发布包或链接时，用 send_to_agent 向 @工作台用户 发一条清单：文件放 files，完整 HTTP(S) 网址写进 message。只有网址也要发。群聊交办时关联原 MSG_ID，CLI 直接交办时省略 replyTo。跨端按原授权回信。" +
                "开始收到的任务用 report_task_status(MSG_ID, working)；无须回信用 completed 结案，需回信用 send_to_agent 的 replyTo 关联原 MSG_ID。中途回信加 finalReply=false。更新需求默认 supersedePrevious=true；独立任务设为 false。交接、审核或交付图片时，调用 read_collaboration_skill 读取本窗口的 ff-agent-collab 规范和示例。";
        }, () =>
        {
            var update = updatedIdentity;
            updatedIdentity = null;
            if (receipt != null || !supportsChannel) return update;
            using var state = new WorkbenchStore(root);
            var id = state.NextNativeOffer(grant);
            if (id == null || id == lastHint) return update;
            lastHint = id;
            return (update == null ? "" : update + "\n") + Notice(id);
        }, () => supportsChannel, ct => NotifyInbox(root,grant,ct), success =>
        {
            if (receipt == null) return;
            using var state = new WorkbenchStore(root);
            state.FinishNativeMessage(receipt,success);
            receipt = null;
        });
    }

    private static string Notice(string id) => $"群聊有人 @ 你，请调用 receive_message，messageId 填 {id}。";

    private static string ChannelReceipt(string grant,string id) => Convert.ToHexString(
        System.Security.Cryptography.HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes(grant),System.Text.Encoding.UTF8.GetBytes(id)))[..24];

    private static async Task NotifyInbox(string root, string grant, CancellationToken ct)
    {
        using var state = new WorkbenchStore(root);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        string? notified = null;
        long version = -1;
        do
        {
            var current = state.DataVersion;
            if (current == version) continue;
            version = current;
            string? id;
            try { id = state.NextNativeOffer(grant); }
            catch (WorkbenchError e) when (e.Failure == WorkbenchFailure.Forbidden) { return; }
            if (id != null && id != notified)
            {
                Console.WriteLine(JsonSerializer.Serialize(new {jsonrpc="2.0", method="notifications/claude/channel",
                    @params=new {content=Notice(id) + " 同时传 channelReceipt=" + ChannelReceipt(grant,id),meta=new {message_id=id}}}));
                Console.Out.Flush();
            }
            notified = id;
        } while (await timer.WaitForNextTickAsync(ct));
    }

    private static AgentWindow McpCaller(WorkbenchStore state, string grant)
    {
        var window = state.Caller(grant);
        return window.IsLeader && state.DedicatedReader(window.Id) ? window with { IsLeader = false } : window;
    }

    private static string Call(string root, string grant, string name, JsonNode input, Action<AgentWindow> observeIdentity, int? port)
    {
        using var state = new WorkbenchStore(root);
        var caller = McpCaller(state, grant);
        observeIdentity(caller);
        if (name==CollaborationTools.FinishTopic || name==CollaborationTools.Send && (input["cancel"]!=null || input["resend"]!=null))
            if(state.DedicatedReader(caller.Id)) throw new WorkbenchError("专用审阅会话不支持该操作",WorkbenchFailure.Forbidden);
        switch (name)
        {
            case CollaborationTools.WorkbenchUpdate:
                if(port==null || state.DedicatedReader(caller.Id))
                    throw new WorkbenchError("当前终端没有工作台更新通道",WorkbenchFailure.Forbidden);
                using(var request=new HttpRequestMessage(HttpMethod.Post,$"http://127.0.0.1:{port}/api/updates/agent"))
                {
                    request.Headers.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",grant);
                    request.Content=System.Net.Http.Json.JsonContent.Create(input);
                    using var response=Updates.Send(request);
                    string body=response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    if(!response.IsSuccessStatusCode)
                        throw new WorkbenchError(body.Length==0?"当前 Agent 无权更新工作台":JsonNode.Parse(body)?["error"]?.GetValue<string>()??"更新请求失败");
                    return body;
                }
            case CollaborationTools.ResumeAfterRestart:
                return JsonSerializer.Serialize(new { id=state.SaveRestartTask(grant,WorkbenchInput.Text((string?)input["message"],"恢复任务",4000)),
                    status="awaiting_restart" });
            case CollaborationTools.SendPeerFiles:
                if (input["files"] is not JsonArray attachments) throw new WorkbenchError("请选择要发送的项目附件");
                return JsonSerializer.Serialize(state.QueuePeerAttachments(grant, (string?)input["target"], (string?)input["message"],
                    (string?)input["attachTo"], attachments.Select(item => (string?)item ?? "").ToArray()));
            case CollaborationTools.ShowExternalApproval:
                state.ShowExternalApproval(grant, WorkbenchInput.Id((string?)input["applicationId"]));
                return """{"status":"awaiting_user"}""";
            case CollaborationTools.ReportTask:
                var status=state.ReportTask(grant,WorkbenchInput.Text((string?)input["messageId"],"MSG_ID",32),WorkbenchInput.Text((string?)input["status"],"协作状态",16));
                return JsonSerializer.Serialize(new {status});
            case CollaborationTools.ReadSkill:
                return CollaborationSkill.Read();
            case CollaborationTools.ReadTeamSkill:
                return CollaborationSkill.ReadTeam(caller.IsLeader);
            case CollaborationTools.ReadKnowledgeSkill:
                return CollaborationSkill.ReadFile("ff-acp-knowledge");
            case CollaborationTools.KnowledgeSearch:
                return JsonSerializer.Serialize(WorkbenchKnowledge.Search(state, caller.Project,
                    input.Deserialize<KnowledgeQuery>(KnowledgeJson)!, caller), KnowledgeJson);
            case CollaborationTools.KnowledgeRead:
                return JsonSerializer.Serialize(WorkbenchKnowledge.Read(state, caller.Project,
                    WorkbenchInput.Text((string?)input["path"], "知识库路径", 512), caller), KnowledgeJson);
            case CollaborationTools.KnowledgeWrite:
                return JsonSerializer.Serialize(WorkbenchKnowledge.Write(state, caller.Project,
                    input.Deserialize<KnowledgeWrite>(KnowledgeJson)!, caller, grant), KnowledgeJson);
            case CollaborationTools.KnowledgeTeam:
                return JsonSerializer.Serialize(WorkbenchKnowledge.TeamContext(state, caller), KnowledgeJson);
            case CollaborationTools.AvailableEngines:
                if (state.DedicatedReader(caller.Id))
                    throw new WorkbenchError("专用审阅会话不可查询本机引擎账号与模型", WorkbenchFailure.Forbidden);
                return JsonSerializer.Serialize(AvailableEngines(caller.Project, grant, port), KnowledgeJson);
            case CollaborationTools.ProposeTeam:
                var proposal = state.ProposeTeam(grant, input.Deserialize<TeamProposalInput>(TeamInputJson)
                    ?? throw new WorkbenchError("请填写团队提案"));
                return JsonSerializer.Serialize(new { proposalId = proposal.Id, status = "awaiting_user_confirmation", count = proposal.Members.Length });
            case CollaborationTools.FinishTopic:
                var finished = state.FinishOwnTopic(grant, WorkbenchInput.Text((string?)input["summary"], "完成摘要", 2000));
                return JsonSerializer.Serialize(new { id = finished.Id, status = finished.Status, summary = finished.FinishSummary });
            case CollaborationTools.ListWindows:
                if(state.DedicatedReader(caller.Id))
                    return state.CurrentPeerRequest(caller.Id) is string route
                        ? JsonSerializer.Serialize(new[]{new {name="@reply-"+route,online=true}}) : "[]";
                bool all = (bool?)input["allTopics"] == true;
                var topics = state.Topics().ToDictionary(t => t.Id, t => t.Name);
                var peers = state.Windows().Where(a => (all || a.TopicId == caller.TopicId) && state.CanContact(caller,a));
                return JsonSerializer.Serialize(peers.Select(a =>
                {
                    var item = new Dictionary<string, object>
                    {
                        ["name"] = "@" + (a.TopicId == caller.TopicId ? "" : topics[a.TopicId] + "/") + a.Name,
                        ["online"] = a.Online
                    };
                    if (a.Role != null) item["role"] = a.Role;
                    if (a.IsLeader) item["leader"] = true;
                    if (all) item["topic"] = topics[a.TopicId];
                    if ((bool?)input["detail"] == true)
                    {
                        item["id"] = a.Id;
                        item["engine"] = a.Engine;
                        item["project"] = a.Project;
                    }
                    return item;
                }).Concat((caller.IsLeader ? state.RemoteRecipients(caller.TopicId,caller.Id) : []).Select(r =>
                {
                    var item = new Dictionary<string,object> { ["name"]="@"+r.AgentName, ["online"]=r.Online, ["id"]=r.Id, ["node"]=r.NodeName };
                    if ((bool?)input["detail"]==true) { item["remote"]=true; item["teamMember"]=r.TeamMember; }
                    return item;
                }).Concat((caller.IsLeader ? state.ReturnContacts(caller.Id) : []).Select(c=>new Dictionary<string,object> {
                    ["name"]="@"+c.Name, ["online"]=c.Online, ["id"]=c.Id, ["node"]=c.NodeName, ["returnContact"]=true
                })).Concat(state.GroupRecipients(caller.TopicId,caller.Id).Select(member=>new Dictionary<string,object> {
                    ["name"]="@"+member.Name,["id"]=member.Key,["target"]="@"+member.Key,["topic"]=member.Topic,
                    ["node"]=member.User,["engine"]=member.Engine??"",["online"]=member.Online,["leader"]=member.Leader,
                    ["remote"]=true,["permanentGroup"]=true
                }))));
            case CollaborationTools.UpdateTeam:
                state.UpdateTeamContext(grant,input.AsObject().ContainsKey("goal"),(string?)input["goal"],
                    input["roles"]?.Deserialize<TeamRoleEdit[]>(KnowledgeJson) ?? []);
                return "{\"status\":\"updated\"}";
            case CollaborationTools.Send:
                if (input["targets"] is JsonArray)
                {
                    if (input["target"] != null || input["cancel"] != null || input["resend"] != null || input["replyTo"] != null ||
                        input["finalReply"] != null || input["broadcast"] != null || input["externalTargets"] != null)
                        throw new WorkbenchError("targets 用于向选定成员发送一条新消息");
                    return JsonSerializer.Serialize(HandoffService.SendGroup(state,grant,input["targets"]!.Deserialize<string[]>()!,
                        (string?)input["message"],(string?)input["document"],input["files"]?.Deserialize<string[]>() ?? [],
                        ReturnHint(input),(bool?)input["supersedePrevious"] ?? false),KnowledgeJson);
                }
                if ((bool?)input["broadcast"] == true)
                {
                    if (((string?)input["target"])?.Trim() != HandoffService.AllMembers)
                        throw new WorkbenchError("群发请指定 target=@全部");
                    if (input["cancel"] != null || input["resend"] != null || input["replyTo"] != null || input["finalReply"] != null)
                        throw new WorkbenchError("@全部 只用于发送新消息，请按独立消息编号回信、撤回或重发");
                    return JsonSerializer.Serialize(HandoffService.Broadcast(state, grant, (string?)input["message"],
                        (string?)input["document"], input["files"]?.Deserialize<string[]>() ?? [], ReturnHint(input),
                        input["externalTargets"]?.Deserialize<string[]>() ?? []), KnowledgeJson);
                }
                if (input["externalTargets"] != null) throw new WorkbenchError("仅 @全部 支持指定外部组员");
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
                return Queued(HandoffService.Send(state, grant, (string?)input["target"], (string?)input["message"],
                    (string?)input["document"], (string?)input["resend"], input["files"]?.Deserialize<string[]>() ?? [], ReturnHint(input),
                    (bool?)input["supersedePrevious"] ?? true,(string?)input["replyTo"],(bool?)input["finalReply"]));
            case CollaborationTools.ReadDocument:
                if(state.DedicatedReader(caller.Id))
                    return state.ReadCurrentPeerDocument(grant,WorkbenchInput.Text((string?)input["pathOrId"],"交接文档",4096));
                return state.ReadDocument(grant, WorkbenchInput.Text((string?)input["pathOrId"], "交接文档", 4096));
            default:
                throw new WorkbenchError("未知协作工具");
        }
    }

    private static TeamEngineAvailability[] AvailableEngines(string project, string grant, int? port)
    {
        if (port is int localPort)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{localPort}/api/team/engine-catalog");
            request.Headers.Add("X-FF-Window-Grant", grant);
            try
            {
                using var response = Notifications.Send(request);
                if (response.StatusCode == HttpStatusCode.Forbidden)
                    throw new WorkbenchError("Agent 连接已失效或无法查询本机引擎", WorkbenchFailure.Forbidden);
                if (response.IsSuccessStatusCode && JsonSerializer.Deserialize<TeamEngineAvailability[]>(response.Content.ReadAsStream(), KnowledgeJson) is { } installed)
                    return installed;
            }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException)
            { /* 宿主尚未提供缓存时仍可检查本机已安装的 CLI。 */ }
        }
        return SessionCatalog.TeamEngines(project);
    }

    private static string? ReturnHint(JsonNode input) =>
        input["autoReturnHint"] is JsonValue value && value.TryGetValue<bool>(out var reply)
            ? reply.ToString() : (string?)input["autoReturnHint"];

    private static string Queued(InboxItem delivery) =>
        delivery.QueueHint == null ? JsonSerializer.Serialize(new { id = delivery.Id, status = delivery.State == "delivered" ? "delivered" : "queued", document = delivery.Document })
        : JsonSerializer.Serialize(new { id = delivery.Id, status = "queued", document = delivery.Document, hint = delivery.QueueHint });
}

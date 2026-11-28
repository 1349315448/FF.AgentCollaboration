using System.Text.Json;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Files;

namespace FF.AgentCollaboration.Infrastructure.Storage;

internal sealed partial class WorkbenchStore
{
    private sealed record ChatOrigin(string Id, string Name, string Topic, string Project)
    {
        internal static ChatOrigin From(AgentWindow window) => new(window.Id, window.Name, window.TopicId, window.Project);
    }

    private void InitializeChat()
    {
        Exec("""
        CREATE TABLE IF NOT EXISTS chat_post(
            id TEXT PRIMARY KEY, topic TEXT NOT NULL, source TEXT NOT NULL, sender TEXT NOT NULL,
            project TEXT NOT NULL, body TEXT NOT NULL, files TEXT NOT NULL, reply_to TEXT,
            created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
        );
        CREATE INDEX IF NOT EXISTS chat_post_topic ON chat_post(topic,created_at);
        CREATE TABLE IF NOT EXISTS chat_delivery(
            id TEXT PRIMARY KEY, post TEXT NOT NULL, topic TEXT NOT NULL,
            target TEXT NOT NULL, name TEXT NOT NULL, remote INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS chat_delivery_post ON chat_delivery(post);
        CREATE INDEX IF NOT EXISTS chat_delivery_topic ON chat_delivery(topic,post);
        CREATE TABLE IF NOT EXISTS chat_group_link(
            first TEXT NOT NULL,second TEXT NOT NULL,PRIMARY KEY(first,second),CHECK(first<second)
        );
        """);
        InitializeChatLinkMembers();
        InitializePeerChat();
    }

    internal ChatLink[] ChatLinks(string id)
    {
        RegisteredProject(Topic(id).Project);
        return Read("""
            SELECT t.id,t.name,w.id,w.name FROM chat_group_link l
            JOIN topics t ON t.id=CASE WHEN l.first=$p0 THEN l.second ELSE l.first END
            LEFT JOIN windows w ON w.id=t.leader_window_id AND w.closed=0
            WHERE (l.first=$p0 OR l.second=$p0) AND t.status='active' ORDER BY t.name
            """,r=>new ChatLink(r.GetString(0),r.GetString(1),Optional(r,2),Optional(r,3)),id).ToArray();
    }

    internal void SetChatLink(string from,string to,bool enabled) => Atomic(() =>
    {
        var source = Topic(from);
        var target = Topic(to);
        RegisteredProject(source.Project);
        if (from == to || !ProjectFolders.Same(source.Project,target.Project))
            throw new WorkbenchError("只能关联同一项目中的其他小组",WorkbenchFailure.Forbidden);
        if (enabled && (source.Status != "active" || target.Status != "active"))
            throw new WorkbenchError("只可关联进行中的小组",WorkbenchFailure.Conflict);
        if (enabled) Exec("INSERT OR IGNORE INTO chat_group_link(first,second) VALUES(min($p0,$p1),max($p0,$p1))",from,to);
        else Exec("DELETE FROM chat_group_link WHERE first=min($p0,$p1) AND second=max($p0,$p1)",from,to);
    });

    internal bool CanContact(AgentWindow source, AgentWindow target) =>
        source.Id != target.Id && GroupRoute(Topic(source.TopicId),source.Id,Topic(target.TopicId),target.Id);

    private void RequireGroupRoute(AgentWindow source, AgentWindow target)
    {
        if (!CanContact(source,target))
            throw new WorkbenchError("只可联系本组成员或获准合作的成员。", WorkbenchFailure.Forbidden);
    }

    internal AgentWindow ExternalLeader(string topicId, string? expected = null)
    {
        var topic = Topic(topicId);
        if (topic.Status == "active" && topic.LeaderWindowId is string leader && (expected == null || expected == leader))
            return Window(leader);
        throw new WorkbenchError("跨端协作需要进行中话题的当前组长",WorkbenchFailure.Forbidden);
    }

    private void RequireExternalLeader(AgentWindow window) => ExternalLeader(window.TopicId,window.Id);

    internal AgentWindow ExternalSender(string remote, string? topicId, string? windowId = null)
    {
        var capsule = RemoteCapsules().FirstOrDefault(r => r.Id == remote && r.Status == "approved" &&
            (topicId == null || r.TopicId == topicId) &&
            (windowId == null || Topic(r.TopicId).LeaderWindowId == windowId))
            ?? throw new WorkbenchError("没有有效的跨端协作授权",WorkbenchFailure.Forbidden);
        var leader = ExternalLeader(capsule.TopicId,windowId);
        if (capsule.DeniedAgentIds.Contains(leader.Id) || DedicatedReader(leader.Id))
            throw new WorkbenchError("当前组长的跨端协作权限已失效",WorkbenchFailure.Forbidden);
        return leader;
    }

    private (string? Topic, string? Post) UserDispatchOrigin(string remote, string dispatch) => Read("""
        SELECT p.topic,p.id FROM chat_delivery d JOIN chat_post p ON p.id=d.post
        WHERE d.id=$p0 AND d.target=$p1 AND d.remote=1 AND p.source=$p2
        """, r => ((string?)r.GetString(0),(string?)r.GetString(1)), dispatch,remote,InboxItem.ChatUser).SingleOrDefault();

    internal void RequireUserRemote(string remote, string dispatch)
    {
        var (topic, _) = UserDispatchOrigin(remote,dispatch);
        if (topic == null || Topic(topic).Status != "active" ||
            !RemoteCapsules().Any(r => r.Id == remote && r.TopicId == topic && r.Status == "approved"))
            throw new FederationException(403,"peer_not_approved");
    }

    internal void UpdateTeamContext(string grant, bool changeGoal, string? goal, TeamRoleEdit[] roles)
    {
        if ((!changeGoal && roles.Length == 0) || roles.Length > 50) throw new WorkbenchError("请选择要更新的目标或组员职责");
        if (goal?.Length > 2000) throw new WorkbenchError("话题目标最多 2000 字");
        Atomic(() =>
        {
            var source = Caller(grant);
            RequireExternalLeader(source);
            if (DedicatedReader(source.Id)) throw new WorkbenchError("专用审阅会话无团队编辑权限",WorkbenchFailure.Forbidden);
            if (changeGoal) Exec("UPDATE topics SET goal=$p1 WHERE id=$p0",source.TopicId,goal?.Trim());
            foreach (var edit in roles)
            {
                var window = Window(WorkbenchInput.Id(edit.AgentId));
                if (window.TopicId != source.TopicId)
                    throw new WorkbenchError("只能编辑本组成员职责",WorkbenchFailure.Forbidden);
                Exec("UPDATE windows SET role=$p1 WHERE id=$p0 AND closed=0",window.Id,WorkbenchInput.AgentRole(edit.Role));
            }
        });
    }

    private void SaveChat(string id, ChatOrigin source, string body, string[] attachments, string? replyTo = null) =>
        Exec("INSERT INTO chat_post(id,topic,source,sender,project,body,files,reply_to) VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7)",
            id,source.Topic,source.Id,source.Name,source.Project,body,JsonSerializer.Serialize(attachments),replyTo);

    private void LinkChat(string id, string post, string topic, string target, string name, bool remote = false) =>
        Exec("INSERT INTO chat_delivery(id,post,topic,target,name,remote) VALUES($p0,$p1,$p2,$p3,$p4,$p5)",
            id,post,topic,target,name,remote);

    internal const int ChatUploadLimit = 20 * 1024 * 1024;
    internal string PostUserChat(string topicId, ChatInput input, ChatUpload[] uploads)
    {
        string body = string.IsNullOrWhiteSpace(input.Text) && ((input.Files?.Length ?? 0) + uploads.Length > 0)
            ? "发送了附件" : WorkbenchInput.Text(input.Text,"消息",120_000);
        string[] targets = (input.Targets ?? []).Select(WorkbenchInput.Id).Distinct().ToArray();
        string[] files = input.Files ?? [];
        if (targets.Length > 50 || files.Length + uploads.Length > 20) throw new WorkbenchError("每条消息最多 50 位收件人及 20 个附件");
        if (uploads.Sum(file => file.Content.Length) > ChatUploadLimit) throw new WorkbenchError("单条消息上传上限为 20 MiB");
        string post = Guid.NewGuid().ToString("N");
        var created = new List<string>();
        try
        {
            Atomic(() =>
            {
                var topic = Topic(topicId);
                if (topic.Status != "active") throw new WorkbenchError("请先重新打开这个话题",WorkbenchFailure.Conflict);
                var source = new ChatOrigin(InboxItem.ChatUser,"工作台用户",topic.Id,RegisteredProject(topic.Project));
                string[] attachments = files.Select(file => ProjectFolders.Attachment(source.Project,file)).Distinct(ProjectFolders.Comparer).ToArray();
                var remotes = RemoteCapsules().Where(r => r.TopicId == topicId && r.Status == "approved").ToDictionary(r => r.Id);
                var groups=GroupRecipients(topicId).ToDictionary(member=>member.Key);
                var local = targets.Where(id => !remotes.ContainsKey(id) && !groups.ContainsKey(id)).Select(Window).ToArray();
                foreach (var agent in local)
                    if (!GroupRoute(topic,InboxItem.ChatUser,Topic(agent.TopicId),agent.Id) || DedicatedReader(agent.Id))
                        throw new WorkbenchError("只能选择本组成员或获准合作的成员",WorkbenchFailure.Forbidden);
                if (targets.Any(remotes.ContainsKey) && attachments.Length + uploads.Length != 0)
                    throw new WorkbenchError("跨端附件请由 Agent 使用已授权的文件发送入口");
                if (input.HumanPeer != null && (targets.Length != 0 || attachments.Length + uploads.Length != 0 || body.Length > 8000))
                    throw new WorkbenchError("联系对端用户时发送 8000 字以内的文本");
                string[] uploaded = uploads.Select((file,index) => WriteChatUpload(post,file,index,created)).ToArray();
                SaveChat(post,source,body,[..attachments,..uploaded]);
                string? hint = input.RequestReply
                    ? $"请用 {CollaborationTools.Send} 向 {InboxItem.ChatUserAddress} 回报最终结果，replyTo 填本条 MSG_ID。"
                    : null;
                foreach (var agent in local)
                {
                    var item = EnqueueLocal(source,agent,body,attachments,hint,null,false,null,true,created,uploaded);
                    LinkChat(item.Id,post,agent.TopicId,agent.Id,agent.Name);
                }
                foreach (var remote in targets.Where(remotes.ContainsKey).Select(id => remotes[id]))
                    QueueChatRemote(post,topic,remote,body,input.RequestReply);
                foreach (string id in targets.Where(groups.ContainsKey))
                    QueueGroupMessage(groups[id],source,post,body,[..attachments,..uploaded],expectsReply:input.RequestReply);
                if (input.HumanPeer != null) QueueHumanChat(post,topic,input.HumanPeer,body);
            });
            return post;
        }
        catch
        {
            foreach (string path in created) File.Delete(path);
            string folder = ChatUploadDirectory(post);
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
            throw;
        }
    }

    private string ChatUploadDirectory(string post) => Path.Combine(root,"chat-uploads",WorkbenchInput.Id(post));
    private bool ChatUploadPath(string post,string path) => Guid.TryParseExact(post,"N",out _) &&
        ProjectFolders.Same(Path.GetDirectoryName(path),ChatUploadDirectory(post));

    private string WriteChatUpload(string post,ChatUpload upload,int index,List<string> created)
    {
        string name = WorkbenchInput.Text(upload.Name,"文件名",180);
        if (name is "." or ".." || name.IndexOfAny(['/', '\\', ':']) >= 0 || name.Any(char.IsControl) ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new WorkbenchError("附件文件名无效");
        string directory = ChatUploadDirectory(post);
        StorageDirectories.CreatePrivateDirectory(directory);
        using var guard = WorkspaceDirectoryGuard.Open(directory);
        string path = Path.Combine(directory,$"{index:D2}-{name}");
        using var file = guard.Create(path);
        created.Add(path);
        upload.Content.CopyTo(file);
        return path;
    }

    private void QueueChatRemote(string post, WorkbenchTopic topic, RemoteCapsule remote, string body, bool reply)
    {
        string id = Guid.NewGuid().ToString("N");
        string text = "来自工作台用户：" + body;
        string? filename = null;
        byte[]? data = null;
        if (System.Text.Encoding.UTF8.GetByteCount(text) > 2048)
        {
            filename = "handoff.md";
            data = System.Text.Encoding.UTF8.GetBytes(text);
            text = text[..Math.Min(text.Length,220)] + "…";
        }
        Exec("INSERT INTO fed_outbox(id,remote,window_id,body,name,content,expects_reply) VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6)",
            id,remote.Id,InboxItem.ChatUser,text,filename,data,reply);
        Mail(topic.Id,"outgoing",InboxItem.ChatUser,remote.Id,remote.AgentName + " · " + remote.NodeName,text,taskId:id);
        LinkChat(id,post,topic.Id,remote.Id,remote.AgentName,true);
    }

    internal InboxItem[] PostAgentChat(string grant, string[] targets, string body, string[] files, string? document,
        string? hint, bool allMembers, string[] external, bool supersede = false)
    {
        if (targets.Length > 50 || external.Length > 50) throw new WorkbenchError("收件人超过 50 位");
        var created = new List<string>();
        var deliveries = new List<InboxItem>();
        string post = Guid.NewGuid().ToString("N");
        try
        {
            Atomic(() =>
            {
                var source = Caller(grant);
                if (DedicatedReader(source.Id)) throw new WorkbenchError("专用审阅 Agent 仅可回复当前来信",WorkbenchFailure.Forbidden);
                var topic = Topic(source.TopicId);
                if (topic.Status != "active") throw new WorkbenchError("请先重新打开这个话题");
                if (allMembers) RequireBroadcastLeader(source);
                var allowed = RemoteRecipients(topic.Id,source.Id).ToDictionary(r => r.Id);
                if (allMembers)
                    allowed = allowed.Where(r => r.Value.TeamMember).ToDictionary();
                var groups=GroupRecipients(topic.Id,source.Id).ToDictionary(member=>member.Key);
                var designatedGroups=targets.Select(GroupAddress).Where(groups.ContainsKey).Distinct().ToArray();
                if (designatedGroups.Length>0 && files.Length>0 && !AttachmentPermission(source.Id))
                    throw new WorkbenchError("请由用户发送附件，或先为此 Agent 授权文件发送");
                var addresses = targets.Where(address=>!groups.ContainsKey(GroupAddress(address)))
                    .Select(address => (Address:address, Remote:RemoteAddress(source,address))).ToArray();
                var requestedRemote = external.Concat(addresses.Where(item => item.Remote != null).Select(item => item.Remote!.Id)).Distinct().ToArray();
                if (requestedRemote.Any(id => !allowed.ContainsKey(id)))
                    throw new WorkbenchError("请选择当前话题已授权的外部 Agent",WorkbenchFailure.Forbidden);
                if (requestedRemote.Length != 0 && files.Length != 0)
                    throw new WorkbenchError("跨端附件请使用 document 或 send_peer_files");
                var local = allMembers ? Windows().Where(a => a.TopicId == topic.Id && a.Id != source.Id).ToArray()
                    : addresses.Where(item => item.Remote == null).Select(item => Recipient(source,item.Address)).DistinctBy(a => a.Id).ToArray();
                foreach (var agent in local) RequireGroupRoute(source,agent);
                if (local.Length + requestedRemote.Length + designatedGroups.Length == 0) throw new WorkbenchError("请选择收件 Agent");
                var origin = ChatOrigin.From(source);
                string[] attachments = files.Select(file => ProjectFolders.Attachment(source.Project,file)).ToArray();
                string? path = document == null ? null : ProjectMarkdown(source.Project,topic.Folder,document);
                SaveChat(post,origin,body,path == null ? attachments : [path,..attachments]);
                foreach (var agent in local)
                {
                    var item = EnqueueLocal(origin,agent,body,attachments,hint,path,supersede,null,true,created);
                    LinkChat(item.Id,post,agent.TopicId,agent.Id,agent.Name);
                    deliveries.Add(item);
                }
                foreach (string id in requestedRemote)
                    deliveries.Add(QueueFederatedChat(source,allowed[id],body,path,hint,post,supersede));
                foreach (string id in designatedGroups)
                    deliveries.Add(QueueGroupMessage(groups[id],origin,post,body,path==null?attachments:[path,..attachments],
                        expectsReply:hint!=null,supersede:supersede));
            });
            return deliveries.ToArray();
        }
        catch
        {
            foreach (string path in created) File.Delete(path);
            throw;
        }
    }

    internal InboxItem ReplyToUserChat(string grant, string reference, string body, string[] files, string? document, bool final)
    {
        string post = Guid.NewGuid().ToString("N");
        string? path = null;
        Atomic(() =>
        {
            var source = Caller(grant);
            var task = ReceivedTaskFor(source.Id,reference);
            if (task.Source != InboxItem.ChatUser) throw new WorkbenchError("原消息的发件人不是工作台用户",WorkbenchFailure.Forbidden);
            var record = Read("SELECT p.id,p.topic FROM chat_post p JOIN chat_delivery d ON d.post=p.id WHERE d.id=$p0",
                row => (Id:row.GetString(0),Topic:row.GetString(1)), task.Id).Single();
            var topic = Topic(source.TopicId);
            if (record.Topic != topic.Id)
            {
                var original = Topic(record.Topic);
                if (!GroupRoute(original,InboxItem.ChatUser,topic,source.Id))
                    throw new WorkbenchError("当前小组已不能跨组回信",WorkbenchFailure.Forbidden);
            }
            path = document == null ? null : ProjectMarkdown(source.Project,topic.Folder,document);
            var attachment = files.Select(file => ProjectFolders.Attachment(source.Project,file)).ToArray();
            var origin = ChatOrigin.From(source);
            SaveChat(post,origin,body,path == null ? attachment : [path,..attachment],record.Id);
            if (record.Topic != topic.Id)
                LinkChat(post,post,record.Topic,InboxItem.ChatUser,"我");
            if (task.ExpectsReply) RecordTaskReply(task,final);
        });
        return new(post,InboxItem.ChatUser,"",body,[],path,null,"delivered");
    }
}

using System.Text.Json;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Files;

namespace FF.AgentCollaboration.Infrastructure.Storage;

internal sealed partial class WorkbenchStore
{
    private const string ChatRows = """
        SELECT p.id,p.topic,p.source,p.sender,p.project,p.body,p.created_at,p.reply_to,p.files,
               'post' AS kind,NULL AS peer,NULL AS application,NULL AS target,NULL AS recipient,NULL AS state,NULL AS work,
               0 AS acknowledged,original.topic AS reply_topic,group_mail.id IS NOT NULL AS group_incoming
        FROM chat_post p LEFT JOIN chat_post original ON original.id=p.reply_to AND original.topic<>p.topic
        LEFT JOIN fed_group_mail group_mail ON group_mail.id=p.id AND group_mail.incoming=1
        WHERE p.topic=$p0 OR EXISTS(SELECT 1 FROM chat_delivery d WHERE d.post=p.id AND d.topic=$p0)
        UNION ALL
        SELECT i.id,coalesce(s.topic_id,i.topic_id),i.source,i.sender,s.project,i.text,
               coalesce(strftime('%Y-%m-%dT%H:%M:%fZ',i.created_at),'1970-01-01T00:00:00.000Z'),
               i.reply_to,i.files,'local',NULL,NULL,i.target,t.name,i.state,i.work_state,i.acknowledged,NULL,0
        FROM inbox i JOIN windows t ON t.id=i.target LEFT JOIN windows s ON s.id=i.source
        WHERE (i.topic_id=$p0 OR s.topic_id=$p0) AND i.source<>'peer-mailbox'
          AND NOT EXISTS(SELECT 1 FROM chat_delivery d WHERE d.id=i.id)
          AND NOT EXISTS(SELECT 1 FROM fed_mail f WHERE f.task_id=i.id AND f.topic=$p0)
        UNION ALL
        SELECT 'peer:'||m.seq,m.topic,m.source,coalesce(sender.name,m.label),NULL,m.body,strftime('%Y-%m-%dT%H:%M:%fZ',m.created),
               m.reply_to,'[]',m.kind,m.seq,CASE WHEN m.kind='application' THEN m.source END,m.target,
               CASE WHEN m.kind IN ('outgoing','reply-out') THEN m.label
                    WHEN m.kind='human-in' OR (m.kind='reply-in' AND m.target='peer-mailbox') THEN '工作台用户'
                    ELSE recipient.name END,NULL,NULL,0,NULL,0
        FROM fed_mail m LEFT JOIN windows sender ON m.kind IN ('reply-out','outgoing') AND sender.id=m.source
        LEFT JOIN windows recipient ON recipient.id=m.target
        WHERE m.topic=$p0 AND NOT EXISTS(SELECT 1 FROM chat_delivery d WHERE d.id=m.task_id AND d.remote=1)
        """;

    private sealed record ChatRow(string Id, string Topic, string Source, string Sender, string? Project, string Body,
        string At, string? Reply, string[] Files, string Kind, long? Peer, string? Application, ChatReceipt? Receipt, string? ReplyTopic,
        bool GroupIncoming, string? Recipient);

    private List<ChatRow> ChatRecords(string topic, string? before, string? exact, int limit, string[]? changes = null)
    {
        RegisteredProject(Topic(topic).Project);
        if (before?.Length > 90 || exact?.Length > 64) throw new WorkbenchError("消息编号无效");
        return Read($"""
            WITH rows AS ({ChatRows}), marked AS (SELECT *, created_at||'/'||id AS position FROM rows)
            SELECT * FROM marked WHERE ($p1 IS NULL OR position<$p1) AND ($p2 IS NULL OR id=$p2)
            AND ($p4 IS NULL OR id IN (SELECT value FROM json_each($p4)))
            ORDER BY position DESC LIMIT $p3
            """, r => new ChatRow(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),Optional(r,4),
                r.GetString(5),r.GetString(6),Optional(r,7),JsonSerializer.Deserialize<string[]>(r.GetString(8))!,
                r.GetString(9),r.IsDBNull(10) ? null : r.GetInt64(10),Optional(r,11),
                r.IsDBNull(14) ? null : new(r.GetString(0),r.GetString(12),r.GetString(13),r.GetString(14),
                    Optional(r,15),r.GetBoolean(16)), Optional(r,17),r.GetBoolean(18),Optional(r,13)), topic,before,exact,limit,changes == null ? null : JsonSerializer.Serialize(changes));
    }

    internal ChatPost[] ChatHistory(string topic, string? before = null, string[]? changes = null)
    {
        if (changes is { Length: > 40 } || changes?.Any(id => id.Length > 64) == true)
            throw new WorkbenchError("最多刷新 40 条消息");
        var rows = ChatRecords(topic,before,null,40,changes);
        var postIds = rows.Where(row=>row.Kind=="post").Select(row=>row.Id).ToArray();
        var addresses = Read("""
            SELECT d.post,CASE WHEN d.target=$p2 THEN '工作台用户' ELSE d.name END
            FROM chat_delivery d JOIN chat_post p ON p.id=d.post
            WHERE d.post IN (SELECT value FROM json_each($p0)) AND (d.topic=$p1 OR p.topic=$p1)
            ORDER BY d.rowid
            """,r=>(Post:r.GetString(0),Name:r.GetString(1)),JsonSerializer.Serialize(postIds),topic,InboxItem.ChatUser)
            .ToLookup(address=>address.Post,address=>address.Name);
        long[] peerRows = rows.Where(row => row.Peer.HasValue).Select(row => row.Peer!.Value).ToArray();
        Dictionary<long,PeerMail> peerReceipts = peerRows.Length == 0 ? [] : RecentPeerMail(topic,peerRows).ToDictionary(mail => mail.Seq);
        return rows.Select(row =>
        {
            ChatReceipt[] receipts = row.Kind == "post" ? Receipts(row.Id,topic,row.Topic)
                : row.Receipt != null ? [row.Receipt]
                : row.Peer is long seq && peerReceipts.TryGetValue(seq,out var mail) && mail.DeliveryState is string state &&
                    mail.Kind is not ("reply-in" or "human-in")
                    ? [new(row.Id,mail.Target,mail.Label,state,mail.WorkState,false)] : [];
            var files = ChatFiles(row,topic);
            string sender = row.Source == InboxItem.ChatUser && row.Kind != "human-in" ? "我" : row.Source=="peer-mailbox" && row.Kind == "outgoing" ? "本机组长" : row.Sender;
            var recipients = row.Kind == "post" ? addresses[row.Id].ToArray()
                : row.Recipient is string recipient ? [recipient] : Array.Empty<string>();
            // 本组 Agent 直接给用户的消息和跨端发给本机用户的消息没有终端投递记录。
            if (recipients.Length==0 && row.Kind=="post" && row.Source!=InboxItem.ChatUser)
                recipients=["工作台用户"];
            return new ChatPost(row.Id,row.Topic,row.Source,sender,row.Body,row.At,row.Reply,
                files.Select(file => Attachment(file,row.GroupIncoming?null:row.Project)).ToArray(),receipts,row.Peer,row.Kind,row.Application,row.ReplyTopic,recipients);
        }).ToArray();
    }

    private ChatReceipt[] Receipts(string message,string topic,string origin) => Read("""
        SELECT d.id,d.target,coalesce(w.name,d.name),
            CASE WHEN g.id IS NOT NULL THEN g.delivery WHEN d.remote=1 THEN coalesce((
                SELECT substr(e.body,10) FROM fed_received e
                WHERE e.remote=d.target AND e.request_id=d.id AND e.kind='receipt' AND e.body LIKE 'delivery:%'
                ORDER BY e.seq DESC LIMIT 1),o.state,h.state,'pending')
                ELSE i.state END,
            CASE WHEN g.id IS NOT NULL THEN g.work WHEN d.remote=1 THEN (
                SELECT substr(e.body,6) FROM fed_received e WHERE e.remote=d.target AND e.request_id=d.id
                AND e.kind='receipt' AND e.body LIKE 'work:%' ORDER BY e.seq DESC LIMIT 1)
                ELSE i.work_state END,coalesce(i.acknowledged,0)
        FROM chat_delivery d LEFT JOIN inbox i ON i.id=d.id AND d.remote=0 LEFT JOIN windows w ON w.id=d.target
        LEFT JOIN fed_group_mail g ON g.id=d.id AND g.incoming=0
        LEFT JOIN fed_outbox o ON o.id=d.id AND d.remote=1
        LEFT JOIN fed_chat_outbox h ON h.id=d.id AND d.remote=1
        WHERE d.post=$p0 AND (d.topic=$p1 OR $p1=$p2) AND (i.id IS NOT NULL OR d.remote=1)
        ORDER BY d.rowid
        """,r=>new ChatReceipt(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),Optional(r,4),r.GetBoolean(5)),
        message,topic,origin).ToArray();

    private sealed record ChatFileEntry(string Key,string Name,string FullPath);

    private ChatFileEntry[] ChatFiles(ChatRow row,string topic)
    {
        if (row.Peer is long seq) return PeerChatFiles(seq,topic);
        if (row.GroupIncoming) return Read("""
            SELECT part.key,f.name,f.relative FROM fed_group_mail m JOIN json_each(m.packet,'$.files') part
            JOIN fed_file f ON f.id=json_extract(part.value,'$.id') AND f.owner=m.link AND f.bound=m.id
            WHERE m.id=$p0 AND m.incoming=1 ORDER BY part.key
            """,r=>new ChatFileEntry(r.GetInt64(0).ToString(System.Globalization.CultureInfo.InvariantCulture),r.GetString(1),
                PeerPath(r.GetString(2))),row.Id).ToArray();
        string[] files = row.Files;
        if (row.Kind != "post")
        {
            var item = Preview(row.Id);
            if (item.Document is string document) files = [document,..item.Files];
        }
        return files.Select((path,index) =>
        {
            string name = Path.GetFileName(path);
            return new ChatFileEntry(index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ChatUploadPath(row.Id,path) ? name[3..] : name,path);
        }).ToArray();
    }

    private ChatFileEntry[] PeerChatFiles(long seq,string topic) => Read("""
        SELECT f.id,f.name,f.relative FROM fed_mail m JOIN fed_mail_file l ON l.seq=m.seq JOIN fed_file f ON f.id=l.file_id
        WHERE m.seq=$p0 AND m.topic=$p1
        UNION ALL
        SELECT f.id,f.name,f.relative FROM fed_mail m JOIN fed_attachment a ON a.local=m.task_id JOIN fed_file f ON f.id=a.file
        WHERE m.seq=$p0 AND m.topic=$p1 AND m.kind='incoming'
        """,r=>new ChatFileEntry(r.GetString(0),r.GetString(1),PeerPath(r.GetString(2))),seq,topic).ToArray();

    private static ChatAttachment Attachment(ChatFileEntry file,string? project)
    {
        string? relative = project == null ? null : Path.GetRelativePath(project,file.FullPath);
        if (relative == ".." || relative?.StartsWith(".." + Path.DirectorySeparatorChar,StringComparison.Ordinal) == true ||
            relative != null && Path.IsPathRooted(relative)) relative = null;
        return new(file.Name,file.Key,relative == null ? null : project,relative?.Replace(Path.DirectorySeparatorChar,'/'));
    }

    internal (string Path,string Name) ChatFile(string topic,string message,string key)
    {
        var row = ChatRecords(topic,null,message,1).SingleOrDefault()
            ?? throw new WorkbenchError("消息不在这个小组",WorkbenchFailure.Forbidden);
        var file = ChatFiles(row,topic).FirstOrDefault(file => file.Key == key)
            ?? throw new WorkbenchError("附件已不可用",WorkbenchFailure.NotFound);
        string path = file.FullPath;
        if (row.Peer != null || row.GroupIncoming || ChatUploadPath(row.Id,path)) { TopicArchive.CheckPath(path); return (path,file.Name); }
        if (row.Kind != "post" && Preview(row.Id).Document is string doc && ProjectFolders.Same(path,doc))
            return (WorkspaceDocument(topic,row.Id),file.Name);
        string project = row.Project ?? throw new WorkbenchError("找不到附件来源",WorkbenchFailure.Forbidden);
        if (Path.GetExtension(path).Equals(".md",StringComparison.OrdinalIgnoreCase))
            return (ProjectMarkdown(project,Topic(row.Topic).Folder,path),file.Name);
        return (ProjectFolders.Attachment(project,path),file.Name);
    }
}

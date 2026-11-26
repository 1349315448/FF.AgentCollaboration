using System.Text;
using System.Text.Json;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Files;

namespace FF.AgentCollaboration.Infrastructure.Storage;

internal sealed partial class WorkbenchStore
{
    private void Mail(string topic, string kind, string source, string target, string label, string text, string? upload = null, string? taskId=null)
    {
        Exec("INSERT INTO fed_mail(topic,kind,source,target,label,body,task_id) VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6)",
            topic, kind, source, target, label, text, taskId);
        if (upload != null) Exec("INSERT INTO fed_mail_file VALUES(last_insert_rowid(),$p0)", upload);
    }

    internal byte[] MailDocument(long seq)
    {
        var relative = Read("SELECT f.relative FROM fed_mail_file m JOIN fed_file f ON f.id=m.file_id WHERE m.seq=$p0", r => r.GetString(0), seq)
            .SingleOrDefault() ?? throw new FederationException(404, "file_expired");
        return ReadPeerBytes(PeerPath(relative));
    }

    internal string MailBody(long seq) => Read("SELECT body FROM fed_mail WHERE seq=$p0", row=>row.GetString(0), seq)
        .SingleOrDefault() ?? throw new WorkbenchError("消息不存在", WorkbenchFailure.NotFound);

    private List<PeerMail> RecentPeerMail() => Read("""
        WITH recent AS MATERIALIZED (
            SELECT * FROM fed_mail ORDER BY seq DESC LIMIT 100
        ), receipts AS MATERIALIZED (
            SELECT m.*, i.work_state, i.acknowledged, i.state AS local_delivery, o.state AS dispatch,
                   (SELECT substr(e.body,6) FROM fed_received e WHERE e.remote=m.target AND e.request_id=m.task_id
                    AND e.kind='receipt' AND e.body IN ('work:queued','work:working','work:completed','work:replied')
                    ORDER BY e.seq DESC LIMIT 1) AS remote_work,
                   (SELECT substr(e.body,10) FROM fed_received e WHERE e.remote=m.target AND e.request_id=m.task_id
                    AND e.kind='receipt' AND e.body IN ('delivery:pending','delivery:writing','delivery:delivered','delivery:delivery_uncertain','delivery:ignored')
                    ORDER BY e.seq DESC LIMIT 1) AS remote_delivery
            FROM recent m LEFT JOIN inbox i ON m.kind IN ('incoming','reply-in') AND i.id=m.task_id
            LEFT JOIN fed_outbox o ON m.kind='outgoing' AND o.remote=m.target AND o.id=m.task_id
        )
        SELECT seq,topic,kind,source,target,label,substr(body,1,200),strftime('%Y-%m-%dT%H:%M:%SZ',created),
               EXISTS(SELECT 1 FROM fed_mail_file l JOIN fed_file f ON f.id=l.file_id WHERE l.seq=m.seq),
               CASE WHEN acknowledged=0 THEN work_state
                    WHEN kind='outgoing' AND remote_delivery IS NOT 'ignored' AND (dispatch IS NULL OR dispatch IN ('accepted','pending','sending'))
                    THEN coalesce(remote_work,CASE WHEN dispatch IN ('pending','sending') THEN 'queued' END) END,
               CASE WHEN acknowledged=1 THEN 'ignored' WHEN local_delivery IS NOT NULL THEN local_delivery
                    WHEN kind='outgoing' AND task_id IS NOT NULL THEN coalesce(remote_delivery,dispatch,'accepted')
                    WHEN kind='reply-in' THEN 'accepted' WHEN kind='reply-out' THEN 'sent'
                    WHEN kind='blocked' THEN 'blocked' END
        FROM receipts m ORDER BY seq DESC
        """, r => new PeerMail(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
            r.GetString(6), r.GetString(7), r.GetBoolean(8), Optional(r,9), Optional(r,10)));

    internal bool DedicatedReader(string window) => FedSetting("peer_reader." + window) == "1";
    internal string? CurrentPeerRequest(string window) => Read("""
        SELECT f.local FROM inbox i JOIN fed_incoming f ON f.local=i.id JOIN fed_grant g ON g.id=f.grant_id
        WHERE i.target=$p0 AND i.acknowledged=0 AND f.active>0 AND g.status='approved' AND g.paused=0
        """, r => r.GetString(0), window).SingleOrDefault();

    internal string ReadCurrentPeerDocument(string grant, string address)
    {
        string? id = CurrentPeerRequest(Caller(grant).Id);
        if (id != null && Preview(id).Document is string file && (address == id || address == id[..8] || string.Equals(address, file, StringComparison.OrdinalIgnoreCase)))
            return Encoding.UTF8.GetString(ReadPeerBytes(file));
        throw new WorkbenchError("仅可读取当前外部任务的交接文档", WorkbenchFailure.Forbidden);
    }
    internal void UploadBlocked(string owner, string reason)
    {
        var grant = Grant(owner);
        Mail(Window(grant.WindowId).TopicId, "blocked", "peer-mailbox", grant.WindowId, grant.NodeName, reason);
    }
    internal IEnumerable<RemoteCapsule> RemoteRecipients(string topic) =>
        RemoteCapsules().Where(r => r.TopicId == topic && r.Status == "approved");

    private void QueueIncoming(string id, AgentWindow w, string source, string label, string body, string? path, string? hint)
    {
        if (Topic(w.TopicId).Status != "active") throw new FederationException(409, "topic_closed");
        Exec("""
            INSERT INTO inbox(id,source,target,sender,text,files,document,return_hint,state,document_ref,topic_id,created_at,wake_requested)
            VALUES($p0,$p1,$p2,$p3,$p4,'[]',$p5,$p6,'pending',$p7,$p8,CURRENT_TIMESTAMP,$p9)
            """, id, source, w.Id, label, body, path != null, hint, path, w.TopicId, w.AutoReceive);
    }

    private void QueuePeerRequest(PeerGrant grant, PeerMessage input, string body)
    {
        var window = Window(grant.WindowId);
        string id = Guid.NewGuid().ToString("N");
        string? path = input.UploadId == null ? null : BindPeerFile(grant.Id, input.UploadId, id);
        string label = grant.NodeName + "#" + grant.NodeId[..8];
        string header = grant.PermissionMode == "read_only"
            ? "[EXTERNAL_COLLAB_POLICY: READ_ONLY | DO NOT EDIT/WRITE/COMMIT/PUSH ANY FILES] " : "[EXTERNAL_COLLAB_POLICY: READ_WRITE] ";
        string? contactKey = input.ContactKey == null ? null : WorkbenchInput.Id(input.ContactKey);
        if (contactKey != null) header += "[RETURN_CONTACT: " + ReturnContactId(grant.Id,contactKey) + "] ";
        Exec("INSERT INTO fed_incoming(local,grant_id,request,upload,contact_key) VALUES($p0,$p1,$p2,$p3,$p4)", id, grant.Id, input.Id, input.UploadId, contactKey);
        QueueIncoming(id, window, "peer-mailbox", label, header + body, path, input.ExpectsReply ? "请用 send_to_agent 向 @reply-" + id + " 回信，replyTo 填本条 MSG_ID，可附 Markdown 文档。" : null);
        Exec("UPDATE inbox SET work_state='queued' WHERE id=$p0",id);
        Mail(window.TopicId, "incoming", "peer-mailbox", window.Id, label, body, input.UploadId,id);
    }

    private void WithdrawPeerInbox(string grant)
    {
        Exec("UPDATE fed_incoming SET active=0 WHERE grant_id=$p0", grant);
        Exec("""
        UPDATE inbox SET acknowledged=1,wake_requested=0 WHERE state='pending' AND id IN(SELECT local FROM fed_incoming WHERE grant_id=$p0)
        """, grant);
    }

    private static async Task<string[]?> AuditSnapshot(string project, CancellationToken ct)
    {
        try { return await new GitWorkspaceService(project).DirtyEntries(ct); }
        catch (Exception e) when (!ct.IsCancellationRequested && e is WorkbenchError or IOException or UnauthorizedAccessException or OperationCanceledException or System.ComponentModel.Win32Exception)
        { return ["<audit-unavailable>"]; }
    }

    internal async Task BeginPeerAudit(string id, CancellationToken ct)
    {
        string? project = Read("""
            SELECT w.project FROM fed_incoming f JOIN fed_request r ON r.grant_id=f.grant_id AND r.id=f.request
            JOIN inbox i ON i.id=f.local JOIN windows w ON w.id=i.target
            WHERE f.local=$p0 AND f.baseline IS NULL AND f.audit_done=0 AND r.permission='read_only'
            """, r => r.GetString(0), id).SingleOrDefault();
        if (project == null) return;
        var baseline = await AuditSnapshot(project, ct);
        Exec("UPDATE fed_incoming SET baseline=$p1,audit_done=$p2 WHERE local=$p0 AND baseline IS NULL",
            id, baseline == null ? null : JsonSerializer.Serialize(baseline), baseline == null ? 2 : 0);
    }

    internal async Task CheckPeerAudits(Func<string, string> activity, CancellationToken ct)
    {
        foreach (var current in Read("SELECT f.local,i.target,f.active FROM fed_incoming f JOIN inbox i ON i.id=f.local WHERE f.active>0 AND i.acknowledged=0 AND i.work_state IS NOT 'working'",
            r => (Id: r.GetString(0), Target: r.GetString(1), Phase: r.GetInt32(2))))
        {
            string state = activity(current.Target);
            if (current.Phase == 1 && state == "working") Exec("UPDATE fed_incoming SET active=2 WHERE local=$p0 AND active=1", current.Id);
            else if (current.Phase == 2 && state == "idle") Exec("UPDATE fed_incoming SET active=0 WHERE local=$p0 AND active=2", current.Id);
        }
        var pending = Read("""
            SELECT f.local,i.target,w.project,w.topic_id,f.baseline,f.audit_done,
                   (i.work_state IN ('completed','replied') OR EXISTS(SELECT 1 FROM fed_event e WHERE e.grant_id=f.grant_id AND e.request_id=f.request AND e.kind='reply' AND e.final_reply IS NOT 0)),
                   i.work_state IS 'working'
            FROM fed_incoming f JOIN inbox i ON i.id=f.local JOIN windows w ON w.id=i.target
            WHERE f.baseline IS NOT NULL AND f.audit_done<2 AND i.acknowledged=0 AND i.state IN ('writing','delivered','delivery_uncertain')
            """, r => (Id: r.GetString(0), Target: r.GetString(1), Project: r.GetString(2), Topic: r.GetString(3), Before: r.GetString(4), Phase: r.GetInt32(5), Replied: !r.IsDBNull(6) && r.GetBoolean(6), InProgress: r.GetBoolean(7)));
        foreach (var row in pending)
        {
            var state = activity(row.Target);
            if (!row.Replied && state == "working" && row.Phase == 0) Exec("UPDATE fed_incoming SET audit_done=1 WHERE local=$p0", row.Id);
            if (!row.Replied && (state != "offline" && row.InProgress || !(row.Phase == 1 && state is "idle" or "offline"))) continue;
            var after = await AuditSnapshot(row.Project, ct);
            var before = JsonSerializer.Deserialize<string[]>(row.Before)!;
            var added = after?.Except(before, StringComparer.Ordinal).Take(5).ToArray() ?? [];
            Atomic(() =>
            {
                if (Exec("UPDATE fed_incoming SET audit_done=2 WHERE local=$p0 AND audit_done<2", row.Id) != 1) return;
                bool unavailable = after == null || after.Contains("<audit-unavailable>") || before.Contains("<audit-unavailable>");
                if (unavailable || added.Length > 0)
                    Mail(row.Topic, "audit", row.Target, "peer-mailbox", "只读任务变更核对", unavailable ? "无法核实 Git 状态，请检查工作区" : string.Join(" · ", added));
            });
        }
    }

    internal InboxItem? SendFederated(string callerGrant, string address, string body, string[] files, string? document, string? hint, bool supersede, string? replyTo, bool? finalReply)
    {
        var caller = Caller(callerGrant);
        string name = address.TrimStart('@');
        bool replying = name.StartsWith("reply-", StringComparison.Ordinal) && Guid.TryParseExact(name[6..], "N", out _);
        if (!replying)
        {
            var contacts = ReturnContacts(caller.Id);
            var contact = ReservedContactName(name) ? contacts.FirstOrDefault(c=>c.Id==name)
                : contacts.FirstOrDefault(c=>c.Name.Equals(name,StringComparison.OrdinalIgnoreCase));
            if (contact != null)
            {
                if (name != contact.Id && ContactNameCollides(caller,contact))
                    throw new WorkbenchError("收件人重名，请使用联系人 ID 或完整的本地话题地址");
                if (replyTo == null) return SendContactNotice(callerGrant,caller,contact,body,document,files,hint);
                var original = ReceivedTaskFor(caller.Id,replyTo);
                if (!Read("""
                    SELECT 1 FROM fed_incoming f JOIN fed_return_contact c ON c.grant_id=f.grant_id AND c.key=f.contact_key
                    WHERE f.local=$p0 AND c.id=$p1
                    """,r=>true,original.Id,contact.Id).Any()) throw new WorkbenchError("该联系人不是原任务发件人",WorkbenchFailure.Forbidden);
                name = "reply-"+original.Id;
                replying = true;
            }
        }
        if (DedicatedReader(caller.Id) && (!replying || CurrentPeerRequest(caller.Id) != name[6..]))
            throw new WorkbenchError("专用审阅会话仅可向当前外部来信地址回信", WorkbenchFailure.Forbidden);
        var remote = replying ? null : RemoteAddress(caller, address);
        if (!replying && remote == null) return null;
        if (replyTo!=null && (!replying || ReceivedTaskFor(caller.Id,replyTo).Id!=name[6..]))
            throw new WorkbenchError("回信目标必须对应原外部任务",WorkbenchFailure.Forbidden);
        if (files.Length != 0) throw new WorkbenchError("跨端收件支持一份 Markdown，请通过 document 传入");
        string id = Guid.NewGuid().ToString("N");
        string? filename = null;
        byte[]? bytes = null;
        if (document != null)
        {
            string path = ProjectMarkdown(caller.Project, Topic(caller.TopicId).Folder, document);
            filename = Path.GetFileName(path);
            bytes = ReadPeerBytes(path);
        }
        else if (Encoding.UTF8.GetByteCount(body) > 2048)
        {
            bytes = Encoding.UTF8.GetBytes(body); filename = "handoff.md"; body = body[..Math.Min(body.Length, 220)] + "…";
        }
        if (hint != null) body += "\n" + hint;
        if (replying)
        {
            var route = Read("""
                SELECT f.grant_id,f.request FROM fed_incoming f JOIN inbox i ON i.id=f.local
                WHERE f.local=$p0 AND i.target=$p1
                """, r => (r.GetString(0), r.GetString(1)), name[6..], caller.Id).SingleOrDefault();
            if (route.Item1 == null) throw new WorkbenchError("当前 Agent 无此回信地址", WorkbenchFailure.Forbidden);
            var g = Grant(route.Item1);
            void Check()
            {
                Caller(callerGrant);
                var current = Grant(g.Id);
                if (DedicatedReader(caller.Id) && CurrentPeerRequest(caller.Id) != name[6..])
                    throw new FederationException(403, "request_finished");
                if (current.Status != "approved" || current.Paused || (bytes != null && !current.AllowUpload))
                    throw new FederationException(403, "reply_disabled");
            }
            string? upload = bytes == null ? null : SavePeerFile(g.Id, g.NodeId, id, filename!, bytes, Check).UploadId;
            Atomic(() => { Check(); RecordPeerReply(g.Id, new(id, body, route.Item2, upload, FinalReply:replyTo==null ? null : finalReply??true)); });
            return new(id, g.Id, caller.Name, body, [], null, hint, "queued");
        }
        if (remote == null) throw new WorkbenchError("找不到远程 Agent");
        Atomic(() =>
        {
            Caller(callerGrant);
            if (!RemoteRecipients(caller.TopicId).Any(r => r.Id == remote.Id)) throw new WorkbenchError("外部授权已失效");
            if (supersede) SupersedePeer(caller.Id, remote.Id);
            Exec("INSERT INTO fed_outbox(id,remote,window_id,body,name,content,expects_reply) VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6)",
                id, remote.Id, caller.Id, body, filename, bytes,hint!=null);
            Mail(caller.TopicId, "outgoing", caller.Id, remote.Id, remote.AgentName + " · " + remote.NodeName, body,taskId:id);
        });
        return new(id, remote.Id, caller.Name, body, [], null, hint, "queued");
    }

    internal List<PeerDispatch> Dispatches() => Read("""
        SELECT id,remote,window_id,body,name,content,expects_reply FROM fed_outbox WHERE state='pending' AND next_try<=unixepoch() ORDER BY rowid LIMIT 8
        """, r => new PeerDispatch(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), Optional(r, 4), r.IsDBNull(5) ? null : (byte[])r[5],r.GetBoolean(6)));

    // 0 尚未发出，1 首次发送中，2 存在可能被对端接受的尝试。后续限流不能撤销此前的未知结果。
    internal bool ClaimDispatch(string id) => Exec("UPDATE fed_outbox SET state='sending',started=CASE WHEN started=0 THEN 1 ELSE 2 END WHERE id=$p0 AND state='pending'", id) == 1;

    private void SupersedePeer(string source, string remote) =>
        Exec("UPDATE fed_outbox SET state='superseded' WHERE window_id=$p0 AND remote=$p1 AND state='pending' AND started=0", source, remote);

    private RemoteCapsule? RemoteAddress(AgentWindow caller, string address)
    {
        string name = address.TrimStart('@');
        var matches = RemoteRecipients(caller.TopicId).Where(r => r.Id == name || r.AgentName.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length > 1 || matches.Length == 1 && matches[0].Id != name && Windows().Any(w => w.TopicId == caller.TopicId && w.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new WorkbenchError("收件名称重复，请使用远程胶囊 ID");
        return matches.FirstOrDefault();
    }
    private string? CancelPeer(string grant, string? id, string? address)
    {
        var caller = Caller(grant);
        if (address != null)
        {
            var remote = RemoteAddress(caller, address);
            if (remote == null) return null;
            id = Read("SELECT id FROM fed_outbox WHERE window_id=$p0 AND remote=$p1 AND state='pending' AND started=0 ORDER BY rowid DESC LIMIT 1",
                r => r.GetString(0), caller.Id, remote.Id).SingleOrDefault() ?? throw new WorkbenchError("没有尚未发送的外部消息", WorkbenchFailure.Conflict);
        }
        if (!Read("SELECT 1 FROM fed_outbox WHERE window_id=$p0 AND id=$p1", r => true, caller.Id, id).Any()) return null;
        if (Exec("UPDATE fed_outbox SET state='cancelled' WHERE window_id=$p0 AND id=$p1 AND state='pending' AND started=0", caller.Id, id) != 1)
            throw new WorkbenchError("消息已进入跨端发送流程，无法撤回", WorkbenchFailure.Conflict);
        return id;
    }
    internal InboxItem? ResendPeer(string grant, string original, string? address, bool supersede)
    {
        var caller = Caller(grant);
        var prior = Read("SELECT remote,body,name,content IS NOT NULL FROM fed_outbox WHERE id=$p0 AND window_id=$p1",
            r => (Remote: r.GetString(0), Body: r.GetString(1), Name: Optional(r, 2), Available: r.GetBoolean(3)), original, caller.Id).SingleOrDefault();
        if (prior.Remote == null) return null;
        if (prior.Name != null && !prior.Available) throw new WorkbenchError("原附件已过期，请重新提供文档");
        var remote = RemoteAddress(caller, address ?? prior.Remote) ?? throw new WorkbenchError("请指定已批准的远程胶囊");
        string id = Guid.NewGuid().ToString("N");
        Atomic(() =>
        {
            Caller(grant);
            if (supersede) SupersedePeer(caller.Id, remote.Id);
            Exec("INSERT INTO fed_outbox(id,remote,window_id,body,name,content,expects_reply) SELECT $p0,$p1,window_id,body,name,content,expects_reply FROM fed_outbox WHERE id=$p2", id, remote.Id, original);
            Mail(caller.TopicId, "outgoing", caller.Id, remote.Id, remote.AgentName, prior.Body,taskId:id);
        });
        return new(id, remote.Id, caller.Name, prior.Body, [], null, null, "queued");
    }

    internal void FinishDispatch(PeerDispatch d, string status)
    {
        if (status is "retry" or "unaccepted")
        {
            Exec("UPDATE fed_outbox SET state='pending',next_try=unixepoch()+15,started=CASE WHEN $p1 AND started=1 THEN 0 ELSE 2 END WHERE id=$p0 AND state='sending'",
                d.Id, status == "unaccepted");
            return;
        }
        Atomic(() =>
        {
            if (Exec("UPDATE fed_outbox SET state=$p1 WHERE id=$p0 AND state='sending'", d.Id, status) == 0) return;
            if (status != "accepted")
            {
                var topic = Read("SELECT topic_id FROM windows WHERE id=$p0", r => r.GetString(0), d.Window).FirstOrDefault();
                if (topic != null) Mail(topic, "blocked", d.Window, d.Remote, "外部投递被拦截", status);
            }
        });
    }
}

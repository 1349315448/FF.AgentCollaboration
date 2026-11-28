using System.Text.Json;
using FF.AgentCollaboration.Domain;

namespace FF.AgentCollaboration.Infrastructure.Storage;

internal sealed partial class WorkbenchStore
{
    internal string AddRemote(string topic, string endpoint, string pin, string provider, string window, string agent, string token, string purpose, ContactBinding? binding,
        string? resumeRemoteId = null)
    {
        string id = Guid.NewGuid().ToString("N");
        Atomic(() =>
        {
            ExternalLeader(topic, binding?.AgentId);
            if (provider == FedSetting("node_id")) throw new WorkbenchError("这是本工作台的 Agent，不能添加为外部协作 Agent。", WorkbenchFailure.Conflict);
            if (resumeRemoteId != null) RequireRemoteResume(resumeRemoteId, topic, provider, window, pin);
            var prior = Read("SELECT id,pin FROM fed_remote WHERE provider=$p0 AND window_id=$p1 AND status IN ('applying','pending','approved')",
                r => (r.GetString(0), r.GetString(1)), provider, window).SingleOrDefault();
            if (prior.Item1 != null)
            {
                if (!string.Equals(prior.Item2, pin, StringComparison.OrdinalIgnoreCase)) throw new FederationException(409, "certificate_changed");
                id = prior.Item1;
                Exec("UPDATE fed_remote SET endpoint=$p1,share=CASE WHEN status='applying' THEN $p2 ELSE NULL END WHERE id=$p0", id, endpoint, token);
            }
            else Exec("""
                INSERT INTO fed_remote(id,window_id,endpoint,pin,provider,share,claim,purpose,name,agent,status)
                VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,'',$p8,'applying')
                """, id, window, endpoint, pin, provider, token, NewGrant(), purpose, agent);
            Exec("INSERT OR IGNORE INTO fed_remote_topic VALUES($p0,$p1)", id, topic);
            if (binding != null) BindContact(id,binding);
        });
        return id;
    }

    internal void RequireRemoteResume(string id, string topic, string provider, string window, string pin)
    {
        var existing = Remote(WorkbenchInput.Id(id));
        if (existing?.Status != "approved" || existing.Provider != provider || existing.WindowId != window ||
            !Read("SELECT 1 FROM fed_remote_topic WHERE remote=$p0 AND topic=$p1", _ => true, id, topic).Any())
            throw new WorkbenchError("请贴入同一个外部 Agent 的新分享链接", WorkbenchFailure.Conflict);
        if (!string.Equals(existing.Pin, pin, StringComparison.OrdinalIgnoreCase))
            throw new FederationException(409, "certificate_changed");
    }

    internal List<string> ActiveRemotes() => Read("SELECT id FROM fed_remote WHERE status IN ('applying','pending','approved') AND (contacts_published=1 OR id IN (SELECT remote FROM fed_remote_topic))", r => r.GetString(0));
    internal RemoteConnection? Remote(string id,bool includeWithdrawal=false) => Read("""
        SELECT id,window_id,endpoint,pin,provider,share,claim,token,status,purpose,cursor,NOT EXISTS(SELECT 1 FROM fed_remote_topic WHERE remote=$p0)
        FROM fed_remote WHERE id=$p0 AND (EXISTS(SELECT 1 FROM fed_remote_topic WHERE remote=$p0) OR ($p1 AND contacts_published=1))
        """, r => new RemoteConnection(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
        Optional(r, 5), r.GetString(6), Optional(r, 7), r.GetString(8), r.GetString(9), r.GetInt64(10),r.GetBoolean(11)), id,includeWithdrawal).SingleOrDefault();

    private List<RemoteCapsule> RemoteCapsules() => Read("""
        SELECT r.id,t.topic,r.provider,r.name,r.agent,r.status,r.online,r.connected,r.last_reply,r.error,r.quota_snapshot,b.agent,
            (SELECT json_group_array(d.agent) FROM fed_remote_denial d WHERE d.remote=r.id),
            EXISTS(SELECT 1 FROM team_remote_members m WHERE m.remote_id=r.id AND m.topic_id=t.topic),r.group_name,r.engine
        FROM fed_remote r JOIN fed_remote_topic t ON t.remote=r.id LEFT JOIN fed_contact_binding b ON b.remote=r.id AND b.topic=t.topic ORDER BY r.rowid
        """, r => new RemoteCapsule(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
        r.GetBoolean(6), r.GetBoolean(7), Optional(r, 8), Optional(r, 9),
        r.GetString(5) == "approved" && !r.IsDBNull(10) ? JsonSerializer.Deserialize<PeerQuota>(r.GetString(10)) : null, Optional(r,11),
        JsonSerializer.Deserialize<string[]>(r.GetString(12))!,r.GetBoolean(13),Optional(r,14),Optional(r,15)));

    internal string? ManualContactAgent(string remote, string? topic) => Read("""
        SELECT b.agent FROM fed_remote_topic t LEFT JOIN fed_contact_binding b ON b.remote=t.remote AND b.topic=t.topic
        WHERE t.remote=$p0 AND ($p1 IS NULL OR t.topic=$p1) ORDER BY t.rowid LIMIT 1
        """, r=>Optional(r,0),remote,topic).SingleOrDefault();

    internal bool RemoteStatus(string id, PeerSession peer, bool connected) => WithRemoteDecision(id,peer.Status,()=>Exec("""
        UPDATE fed_remote SET name=$p1,agent=$p2,status=$p3,online=$p4,connected=$p5,token=coalesce($p6,token),
        group_name=$p9,engine=$p10,quota_snapshot=$p8,share=CASE WHEN $p3='applying' THEN share ELSE NULL END,error=NULL WHERE id=$p0 AND provider=$p7 AND
        (name!=$p1 OR agent!=$p2 OR status!=$p3 OR online!=$p4 OR connected!=$p5 OR ($p6 IS NOT NULL AND token IS NOT $p6) OR quota_snapshot IS NOT $p8 OR group_name IS NOT $p9 OR engine IS NOT $p10 OR error IS NOT NULL)
        """, id, peer.NodeName, peer.AgentName, peer.Status, connected && peer.Online, connected, peer.Token, peer.NodeId,
        peer.Status == "approved" && peer.Quota != null ? JsonSerializer.Serialize(peer.Quota) : null,peer.GroupName,peer.Engine) > 0);

    internal bool RemoteFailure(string id, string reason, string? status = null) => Exec("""
        UPDATE fed_remote SET online=0,connected=0,status=coalesce($p2,status),error=$p1
        WHERE id=$p0 AND (online OR connected OR error IS NOT $p1 OR ($p2 IS NOT NULL AND status!=$p2))
        """, id, reason, status) > 0;

    internal bool RemoteApplicationRejected(string id) =>
        WithRemoteDecision(id,"rejected",()=>RemoteFailure(id,"rejected","rejected"));

    private bool WithRemoteDecision(string id,string? status,Func<bool> update)
    {
        if (status is not ("approved" or "rejected")) return update();
        bool changed=false;
        Atomic(()=>{
            bool awaiting=Read("SELECT 1 FROM fed_remote WHERE id=$p0 AND status IN ('applying','pending')",_=>true,id).Any();
            changed=update();
            if (!changed || !awaiting) return;
            foreach (var link in Read("""
                SELECT t.topic,r.name,r.agent FROM fed_remote_topic t JOIN fed_remote r ON r.id=t.remote WHERE t.remote=$p0
                """,r=>(Topic:r.GetString(0),Name:r.GetString(1),Agent:r.GetString(2)),id))
            {
                string source=string.IsNullOrWhiteSpace(link.Name)?link.Agent:link.Name;
                Mail(link.Topic,"permission",id,InboxItem.ChatUser,source,
                    $"{source} 已{(status=="approved"?"同意":"拒绝")}你与 @{link.Agent} 的 Agent 协作申请。");
            }
        });
        return changed;
    }

    internal bool RemoveRemote(string id, string topic)
    {
        bool teamChanged = false;
        Atomic(() =>
        {
            Exec("DELETE FROM fed_contact_binding WHERE remote=$p0 AND topic=$p1",id,topic);
            teamChanged = Exec("DELETE FROM team_remote_members WHERE remote_id=$p0 AND topic_id=$p1",id,topic) > 0;
            Exec("DELETE FROM fed_remote_topic WHERE remote=$p0 AND topic=$p1", id, topic);
            Exec("UPDATE fed_remote SET online=0,connected=0 WHERE id=$p0 AND NOT EXISTS(SELECT 1 FROM fed_remote_topic WHERE remote=$p0)", id);
        });
        return teamChanged;
    }

    internal void TrackManual(string remote, PeerMessage input)
    {
        var topic = Read("SELECT topic FROM fed_remote_topic WHERE remote=$p0 AND ($p1 IS NULL OR topic=$p1) ORDER BY rowid LIMIT 1", r => r.GetString(0), remote, input.TopicId).SingleOrDefault();
        if (topic == null) throw new FederationException(409, "remote_detached");
        if (Exec("INSERT OR IGNORE INTO fed_manual(remote,id,topic,body,expects_reply) VALUES($p0,$p1,$p2,$p3,$p4)", remote, input.Id, topic, input.Text,input.ExpectsReply) == 0 &&
            !Read("SELECT 1 FROM fed_manual WHERE remote=$p0 AND id=$p1 AND topic=$p2 AND body=$p3 AND expects_reply=$p4", r => true, remote, input.Id, topic, input.Text,input.ExpectsReply).Any())
            throw new FederationException(409, "message_id_conflict");
    }
    internal void ManualSent(string remote, PeerMessage msg) => Atomic(() =>
    {
        if (Exec("UPDATE fed_manual SET sent=1 WHERE remote=$p0 AND id=$p1 AND sent=0", remote, msg.Id) != 1) return;
        var row = Read("SELECT m.topic,r.agent,r.name FROM fed_manual m JOIN fed_remote r ON r.id=m.remote WHERE m.remote=$p0 AND m.id=$p1",
            r => (r.GetString(0), r.GetString(1), r.GetString(2)), remote, msg.Id).Single();
        Mail(row.Item1, "outgoing", "peer-mailbox", remote, row.Item2 + " · " + row.Item3, msg.Text,taskId:msg.Id);
    });
    internal void ReceivePeerEvent(string remote, PeerEvent item) => Atomic(() =>
    {
        if (Exec("""
            UPDATE fed_remote SET cursor=$p1,last_reply=CASE WHEN $p2='reply' THEN $p3 ELSE last_reply END
            WHERE id=$p0 AND cursor<$p1 AND status='approved'
            """, remote, item.Id, item.Kind, item.Text) == 0) return;
        Exec("INSERT INTO fed_received(remote,seq,kind,request_id,body,final_reply) VALUES($p0,$p1,$p2,$p3,$p4,$p5)", remote, item.Id, item.Kind, item.RequestId, item.Text, item.FinalReply);
        if (item.Kind == "contact") ReceiveContactNotice(remote,item);
        if (item.Kind == "contact-removed") ReceiveContactRemoval(remote,item);
        if (item.Kind == "human-chat") ReceiveHumanChat(remote,item);
        if (item.Kind != "reply") return;
        if (item.DocumentId != null)
        {
            if (!Guid.TryParseExact(item.DocumentId, "N", out var documentId))
            {
                RoutePeerReply(remote, item with { Text = item.Text + "（附件引用无效）" }, null);
                return;
            }
            if (Read("SELECT 1 FROM fed_outbox WHERE remote=$p0 AND id=$p1 UNION ALL SELECT 1 FROM fed_manual WHERE remote=$p0 AND id=$p1", r => true, remote, item.RequestId).Any())
                Exec("INSERT INTO fed_download(remote,seq,doc,name) VALUES($p0,$p1,$p2,$p3)", remote, item.Id, documentId.ToString("N"), item.FileName ?? "report.md");
            return;
        }
        RoutePeerReply(remote, item, null);
    });

    internal List<(string Remote, PeerEvent Item)> Downloads() => Read("""
        SELECT d.remote,d.seq,e.request_id,e.body,d.doc,d.name,e.final_reply FROM fed_download d JOIN fed_received e ON e.remote=d.remote AND e.seq=d.seq
        WHERE d.next_try<=unixepoch() ORDER BY d.rowid LIMIT 4
        """, r => (r.GetString(0), new PeerEvent(r.GetInt64(1), "reply", r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetBoolean(6))));
    internal void RetryDownload(string remote, long seq) =>
        Exec("UPDATE fed_download SET next_try=unixepoch()+30 WHERE remote=$p0 AND seq=$p1", remote, seq);
    internal void FinishDownload(string remote, PeerEvent item, string? upload) => Atomic(() =>
    {
        if (Exec("DELETE FROM fed_download WHERE remote=$p0 AND seq=$p1", remote, item.Id) != 0) RoutePeerReply(remote, item, upload);
    });

    private void RoutePeerReply(string remote, PeerEvent item, string? upload)
    {
        var (userTopic, userPost) = UserDispatchOrigin(remote,item.RequestId);
        var target = Read("""
            SELECT w.id,r.name,r.agent,w.closed,w.topic_id FROM fed_outbox o JOIN windows w ON w.id=o.window_id
            JOIN fed_remote r ON r.id=o.remote WHERE o.remote=$p0 AND o.id=$p1
            """, r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetBoolean(3), r.GetString(4)), remote, item.RequestId).SingleOrDefault();
        if (target.Item1 == null || userTopic != null)
        {
            var manual = Read("""
                SELECT coalesce($p2,m.topic),r.agent,r.name FROM fed_remote r
                LEFT JOIN fed_manual m ON m.remote=r.id AND m.id=$p1
                WHERE r.id=$p0 AND ($p2 IS NOT NULL OR m.topic IS NOT NULL)
                """,
                r => (r.GetString(0), r.GetString(1), r.GetString(2)), remote, item.RequestId, userTopic).SingleOrDefault();
            if (manual.Item1 != null)
            {
                if (upload != null) BindPeerFile(remote, upload, item.RequestId);
                Mail(manual.Item1, "reply-in", remote, "peer-mailbox", manual.Item2 + " · " + manual.Item3, item.Text, upload, replyTo:userPost ?? item.RequestId);
            }
            return;
        }
        string label = target.Item3 + " · " + target.Item2;
        string id = Guid.NewGuid().ToString("N");
        string? document = upload == null ? null : BindPeerFile(remote, upload, id);
        Mail(target.Item5, "reply-in", remote, target.Item1, label, item.Text, upload, id, item.RequestId);
        if (target.Item4 || Topic(target.Item5) is not { Status:"active" } team || team.LeaderWindowId != target.Item1) return;
        QueueIncoming(id, Window(target.Item1), remote, label, "[EXTERNAL_REPLY] " + item.Text, document, null);
        Exec("UPDATE inbox SET reply_to=$p1,final_reply=$p2 WHERE id=$p0", id, item.RequestId, item.FinalReply);
    }
}

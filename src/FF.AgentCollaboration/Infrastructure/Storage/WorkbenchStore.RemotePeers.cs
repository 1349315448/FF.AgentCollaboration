using System.Text.Json;
using FF.AgentCollaboration.Domain;

namespace FF.AgentCollaboration.Infrastructure.Storage;

internal sealed partial class WorkbenchStore
{
    internal string AddRemote(string topic, string endpoint, string pin, string provider, string window, string agent, string token, string purpose, ContactBinding? binding)
    {
        string id = Guid.NewGuid().ToString("N");
        Atomic(() =>
        {
            if (Topic(topic).Status != "active") throw new WorkbenchError("请先重新打开话题");
            if (provider == FedSetting("node_id")) throw new WorkbenchError("这是本工作台的 Agent，不能添加为外部协作 Agent。", WorkbenchFailure.Conflict);
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

    internal List<string> ActiveRemotes() => Read("SELECT id FROM fed_remote WHERE status IN ('applying','pending','approved') AND (contacts_published=1 OR id IN (SELECT remote FROM fed_remote_topic))", r => r.GetString(0));
    internal RemoteConnection? Remote(string id,bool includeWithdrawal=false) => Read("""
        SELECT id,window_id,endpoint,pin,provider,share,claim,token,status,purpose,cursor,NOT EXISTS(SELECT 1 FROM fed_remote_topic WHERE remote=$p0)
        FROM fed_remote WHERE id=$p0 AND (EXISTS(SELECT 1 FROM fed_remote_topic WHERE remote=$p0) OR ($p1 AND contacts_published=1))
        """, r => new RemoteConnection(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
        Optional(r, 5), r.GetString(6), Optional(r, 7), r.GetString(8), r.GetString(9), r.GetInt64(10),r.GetBoolean(11)), id,includeWithdrawal).SingleOrDefault();

    private List<RemoteCapsule> RemoteCapsules() => Read("""
        SELECT r.id,t.topic,r.provider,r.name,r.agent,r.status,r.online,r.connected,r.last_reply,r.error,r.quota_snapshot,b.agent
        FROM fed_remote r JOIN fed_remote_topic t ON t.remote=r.id LEFT JOIN fed_contact_binding b ON b.remote=r.id AND b.topic=t.topic ORDER BY r.rowid
        """, r => new RemoteCapsule(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
        r.GetBoolean(6), r.GetBoolean(7), Optional(r, 8), Optional(r, 9),
        r.GetString(5) == "approved" && !r.IsDBNull(10) ? JsonSerializer.Deserialize<PeerQuota>(r.GetString(10)) : null, Optional(r,11)));

    internal bool RemoteStatus(string id, PeerSession peer, bool connected) => Exec("""
        UPDATE fed_remote SET name=$p1,agent=$p2,status=$p3,online=$p4,connected=$p5,token=coalesce($p6,token),
        quota_snapshot=$p8,share=CASE WHEN $p3='applying' THEN share ELSE NULL END,error=NULL WHERE id=$p0 AND provider=$p7 AND
        (name!=$p1 OR agent!=$p2 OR status!=$p3 OR online!=$p4 OR connected!=$p5 OR ($p6 IS NOT NULL AND token IS NOT $p6) OR quota_snapshot IS NOT $p8 OR error IS NOT NULL)
        """, id, peer.NodeName, peer.AgentName, peer.Status, connected && peer.Online, connected, peer.Token, peer.NodeId,
        peer.Status == "approved" && peer.Quota != null ? JsonSerializer.Serialize(peer.Quota) : null) > 0;

    internal bool RemoteFailure(string id, string reason, string? status = null) => Exec("""
        UPDATE fed_remote SET online=0,connected=0,status=coalesce($p2,status),error=$p1
        WHERE id=$p0 AND (online OR connected OR error IS NOT $p1 OR ($p2 IS NOT NULL AND status!=$p2))
        """, id, reason, status) > 0;

    internal void RemoveRemote(string id, string topic) => Atomic(() =>
    {
        Exec("DELETE FROM fed_contact_binding WHERE remote=$p0 AND topic=$p1",id,topic);
        Exec("DELETE FROM fed_remote_topic WHERE remote=$p0 AND topic=$p1", id, topic);
        Exec("UPDATE fed_remote SET online=0,connected=0 WHERE id=$p0 AND NOT EXISTS(SELECT 1 FROM fed_remote_topic WHERE remote=$p0)", id);
    });

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
        var target = Read("""
            SELECT w.id,r.name,r.agent,w.closed,w.topic_id FROM fed_outbox o JOIN windows w ON w.id=o.window_id
            JOIN fed_remote r ON r.id=o.remote WHERE o.remote=$p0 AND o.id=$p1
            """, r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetBoolean(3), r.GetString(4)), remote, item.RequestId).SingleOrDefault();
        if (target.Item1 == null)
        {
            var manual = Read("SELECT m.topic,r.agent,r.name FROM fed_manual m JOIN fed_remote r ON r.id=m.remote WHERE m.remote=$p0 AND m.id=$p1",
                r => (r.GetString(0), r.GetString(1), r.GetString(2)), remote, item.RequestId).SingleOrDefault();
            if (manual.Item1 != null)
            {
                if (upload != null) BindPeerFile(remote, upload, item.RequestId);
                Mail(manual.Item1, "reply-in", remote, "peer-mailbox", manual.Item2 + " · " + manual.Item3, item.Text, upload);
            }
            return;
        }
        string label = target.Item3 + " · " + target.Item2;
        string id = Guid.NewGuid().ToString("N");
        string? document = upload == null ? null : BindPeerFile(remote, upload, id);
        Mail(target.Item5, "reply-in", remote, target.Item1, label, item.Text, upload, id);
        if (target.Item4 || Topic(target.Item5).Status != "active") return;
        QueueIncoming(id, Window(target.Item1), remote, label, "[EXTERNAL_REPLY] " + item.Text, document, null);
        Exec("UPDATE inbox SET reply_to=$p1,final_reply=$p2 WHERE id=$p0", id, item.RequestId, item.FinalReply);
    }
}

using FF.AgentCollaboration.Domain;

namespace FF.AgentCollaboration.Infrastructure.Storage;

internal sealed record PeerHumanChat(string Id, string Text);
internal sealed record PeerHumanReceipt(bool Delivered);
internal sealed record HumanChatDispatch(string Remote, string Id, string Text, bool Receipt);

internal sealed partial class WorkbenchStore
{
    private void InitializePeerChat() => Exec("""
        CREATE TABLE IF NOT EXISTS fed_chat_outbox(
            id TEXT PRIMARY KEY, remote TEXT NOT NULL, leader TEXT NOT NULL, body TEXT NOT NULL,
            direction TEXT NOT NULL, state TEXT NOT NULL DEFAULT 'pending', next_try INTEGER NOT NULL DEFAULT 0,
            chat_to TEXT
        );
        CREATE TABLE IF NOT EXISTS fed_chat_received(
            grant_id TEXT NOT NULL,id TEXT NOT NULL,body TEXT NOT NULL,PRIMARY KEY(grant_id,id)
        );
        """);

    private void QueueHumanChat(string post,WorkbenchTopic topic,string peer,string body)
    {
        var leader = ExternalLeader(topic.Id);
        if (DedicatedReader(leader.Id)) throw new WorkbenchError("请使用可正常协作的当前组长",WorkbenchFailure.Forbidden);
        string[] parts = peer.Split(':',3);
        if (parts.Length < 2) throw new WorkbenchError("请选择已授权的跨端用户");
        string id = WorkbenchInput.Id(parts[1]);
        string delivery = Guid.NewGuid().ToString("N");
        string label;
        if (parts[0] == "remote" && parts.Length == 2)
        {
            var remote = RemoteRecipients(topic.Id,leader.Id).SingleOrDefault(r => r.Id == id)
                ?? throw new WorkbenchError("没有对端用户的有效协作授权",WorkbenchFailure.Forbidden);
            label = remote.NodeName + " · 用户";
            Exec("INSERT INTO fed_chat_outbox(id,remote,leader,body,direction) VALUES($p0,$p1,$p2,$p3,'request')",
                delivery,id,leader.Id,body);
        }
        else if (parts[0] == "grant")
        {
            var grant = Grant(id);
            if (grant.WindowId != leader.Id || grant.Status != "approved" || grant.Paused)
                throw new WorkbenchError("没有对端用户的有效协作授权",WorkbenchFailure.Forbidden);
            string? contact = parts.Length == 3 ? WorkbenchInput.Id(parts[2]) : null;
            var address = Read("""
                SELECT key,name FROM fed_return_contact WHERE grant_id=$p0 AND ($p1 IS NULL OR id=$p1)
                ORDER BY id LIMIT 2
                """,r=>(Key:r.GetString(0),Name:r.GetString(1)),id,contact);
            if (contact != null && address.Count != 1 || address.Count > 1)
                throw new WorkbenchError("请选择有明确回信小组的对端用户",WorkbenchFailure.Conflict);
            var target = address.SingleOrDefault();
            label = grant.NodeName + " · 用户";
            if (target.Name != null) label += " · " + target.Name;
            Exec("INSERT INTO fed_chat_outbox(id,remote,leader,body,direction,chat_to) VALUES($p0,$p1,$p2,$p3,'event',$p4)",
                delivery,id,leader.Id,body,target.Key);
            Exec("INSERT INTO fed_event(grant_id,id,kind,request_id,body) VALUES($p0,$p1,'human-chat',$p1,$p2)",
                id,delivery,body);
        }
        else throw new WorkbenchError("请选择已授权的跨端用户");
        LinkChat(delivery,post,topic.Id,id,label,true);
    }

    internal object AcceptHumanChat(string token,string node,PeerHumanChat message)
    {
        string id = WorkbenchInput.Id(message.Id);
        string body = WorkbenchInput.Text(message.Text,"消息",8000);
        Atomic(() =>
        {
            var grant = Grant(AuthorizePeer(token,node));
            var leader = Window(grant.WindowId);
            RequireExternalLeader(leader);
            if (grant.Paused) throw new FederationException(403,"peer_paused");
            var prior = Read("SELECT body FROM fed_chat_received WHERE grant_id=$p0 AND id=$p1", r=>r.GetString(0),grant.Id,id).SingleOrDefault();
            if (prior != null)
            {
                if (prior != body) throw new FederationException(409,"message_id_conflict");
                return;
            }
            Exec("INSERT INTO fed_chat_received(grant_id,id,body) VALUES($p0,$p1,$p2)",grant.Id,id,body);
            Mail(leader.TopicId,"human-in",grant.Id,InboxItem.ChatUser,grant.NodeName + " · 用户",body,taskId:id);
        });
        return new { id,state = "delivered" };
    }

    private void ReceiveHumanChat(string remote,PeerEvent item)
    {
        string id = WorkbenchInput.Id(item.RequestId);
        string body = WorkbenchInput.Text(item.Text,"消息",8000);
        string? topicId = item.ChatTo == null ? null : Read("""
            SELECT b.topic FROM fed_contact_binding b JOIN topics t ON t.id=b.topic AND t.leader_window_id=b.agent
            WHERE b.remote=$p0 AND b.key=$p1
            """,r=>r.GetString(0),remote,WorkbenchInput.Id(item.ChatTo)).SingleOrDefault();
        var candidates = RemoteCapsules().Where(r => r.Id == remote && r.Status == "approved")
            .Where(r => item.ChatTo == null || r.TopicId == topicId)
            .Where(r => Topic(r.TopicId) is { Status:"active", LeaderWindowId: string leader } && !r.DeniedAgentIds.Contains(leader))
            .Take(2).ToArray();
        var capsule = candidates.Length == 1 ? candidates[0] : null;
        if (capsule != null)
            Mail(capsule.TopicId,"human-in",remote,InboxItem.ChatUser,capsule.NodeName + " · 用户",body,taskId:id);
        Exec("INSERT OR IGNORE INTO fed_chat_outbox(id,remote,leader,body,direction) VALUES($p0,$p1,'',$p2,'receipt')",
            id,remote,capsule == null ? "no_group" : "delivered");
    }

    internal void ConfirmHumanChat(string token,string node,string delivery,bool delivered)
    {
        string grant = AuthorizePeer(token,node);
        Exec("UPDATE fed_chat_outbox SET state=$p2 WHERE id=$p0 AND remote=$p1 AND direction='event' AND state='pending'",
            WorkbenchInput.Id(delivery),grant,delivered ? "delivered" : "no_group");
    }

    internal HumanChatDispatch[] HumanChatDispatches() => Read("""
        SELECT id,remote,body,direction FROM fed_chat_outbox
        WHERE direction IN ('request','receipt') AND state='pending' AND next_try<=unixepoch()
        ORDER BY rowid LIMIT 8
        """,r=>new HumanChatDispatch(r.GetString(1),r.GetString(0),r.GetString(2),r.GetString(3)=="receipt")).ToArray();

    internal void VerifyHumanChatSender(HumanChatDispatch item)
    {
        // A route failure receipt updates only the matching human message on the already authenticated connection.
        if (item.Receipt) return;
        string leader = Read("SELECT leader FROM fed_chat_outbox WHERE id=$p0",r=>r.GetString(0),item.Id).Single();
        var window = Window(leader);
        RequireExternalLeader(window);
        if (!RemoteRecipients(window.TopicId,leader).Any(r => r.Id == item.Remote))
            throw new WorkbenchError("对端授权已失效",WorkbenchFailure.Forbidden);
    }

    internal void FinishHumanChat(string id,string state)
    {
        if (state == "retry") Exec("UPDATE fed_chat_outbox SET next_try=unixepoch()+15 WHERE id=$p0",id);
        else Exec("UPDATE fed_chat_outbox SET state=$p1 WHERE id=$p0",id,state);
    }
}

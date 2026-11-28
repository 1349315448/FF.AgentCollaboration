using System.Security.Cryptography;
using System.Text;
using FF.AgentCollaboration.Domain;

namespace FF.AgentCollaboration.Infrastructure.Storage;

internal sealed partial class WorkbenchStore
{
    internal void SetContactBinding(string remote, ContactBinding input) => Atomic(() => BindContact(remote,input));

    private void BindContact(string remote, ContactBinding input)
    {
        if (!Read("SELECT 1 FROM fed_remote_topic WHERE remote=$p0 AND topic=$p1",r=>true,remote,input.TopicId).Any())
            throw new WorkbenchError("此外部 Agent 不属于当前话题",WorkbenchFailure.NotFound);
        if (input.AgentId == null)
        {
            Exec("DELETE FROM fed_contact_binding WHERE remote=$p0 AND topic=$p1",remote,input.TopicId);
            return;
        }
        var agent = ExternalLeader(input.TopicId,WorkbenchInput.Id(input.AgentId));
        // 换人即换地址，断线期间寄给前任的消息不能投给新联系人。
        Exec("""
            INSERT INTO fed_contact_binding(remote,topic,agent,key) VALUES($p0,$p1,$p2,lower(hex(randomblob(16))))
            ON CONFLICT(remote,topic) DO UPDATE SET agent=excluded.agent,key=excluded.key WHERE agent!=excluded.agent
            """,remote,input.TopicId,agent.Id);
    }

    internal List<AdvertisedContact> AdvertisedContacts(string remote) => Read("""
        SELECT b.key,w.name,w.online,w.id FROM fed_contact_binding b JOIN windows w ON w.id=b.agent AND w.topic_id=b.topic
        JOIN topics t ON t.id=b.topic WHERE b.remote=$p0 AND w.closed=0 AND t.status='active'
        AND t.leader_window_id=w.id ORDER BY b.key
        """,r=>new AdvertisedContact(r.GetString(0),r.GetString(1),r.GetBoolean(2),ScopedContactId(remote,r.GetString(3))),remote);

    internal string? SenderContactKey(string remote,string agent) =>
        Read("SELECT key FROM fed_contact_binding WHERE remote=$p0 AND agent=$p1",r=>r.GetString(0),remote,agent).SingleOrDefault();

    // 来信和联系人列表可能先后到达，用同一授权和绑定计算稳定的收件地址。
    internal static string ScopedContactId(string scope, string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(scope + ":" + key)).AsSpan(0, 16));

    internal void SyncContacts(string token, string node, IReadOnlyList<AdvertisedContact> contacts) => Atomic(() =>
    {
        string grant = AuthorizePeer(token,node);
        var stale = Read("SELECT key FROM fed_return_contact WHERE grant_id=$p0",r=>r.GetString(0),grant).ToHashSet();
        foreach (var contact in contacts)
        {
            if (contact is null) throw new WorkbenchError("回信联系人不能为空");
            string key = WorkbenchInput.Id(contact.Key);
            stale.Remove(key);
            Exec("""
                INSERT INTO fed_return_contact(id,grant_id,key,name,online,sender)
                SELECT $p4,$p0,$p1,$p2,$p3,$p5 WHERE NOT EXISTS(SELECT 1 FROM fed_denied_sender WHERE grant_id=$p0 AND sender=$p5)
                ON CONFLICT(grant_id,key) DO UPDATE SET name=excluded.name,online=excluded.online,sender=excluded.sender
                """,grant,key,WorkbenchInput.AgentName(contact.Name),contact.Online,ScopedContactId(grant,key),
                contact.SenderKey == null ? null : WorkbenchInput.Id(contact.SenderKey));
        }
        foreach (string key in stale) Exec("DELETE FROM fed_return_contact WHERE grant_id=$p0 AND key=$p1",grant,key);
    });

    internal List<ReturnContact> ReturnContacts(string? window=null) => Read("""
        SELECT c.id,g.window_id,c.name,g.name,c.online AND g.streams>0,
               g.paused=0 AND NOT EXISTS(SELECT 1 FROM settings WHERE key='peer_reader.'||g.window_id AND value='1'),c.sender IS NOT NULL,g.id,g.streams>0
        FROM fed_return_contact c JOIN fed_grant g ON g.id=c.grant_id JOIN fed_share s ON s.window_id=g.window_id
        WHERE g.status='approved' AND ($p0 IS NULL OR g.window_id=$p0) ORDER BY c.name,c.id
        """,r=>new ReturnContact(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetBoolean(4),r.GetBoolean(5),r.GetBoolean(6),r.GetString(7),r.GetBoolean(8)),window);

    internal void RemoveReturnContact(string window, string contactId) => Atomic(() =>
    {
        var contact = Read("""
            SELECT c.grant_id,c.sender,c.name,w.name FROM fed_return_contact c JOIN fed_grant g ON g.id=c.grant_id
            JOIN windows w ON w.id=g.window_id WHERE c.id=$p0 AND w.id=$p1 AND g.status='approved'
            """, r=>(Grant:r.GetString(0), Sender:Optional(r,1), Name:r.GetString(2), Provider:r.GetString(3)),contactId,window).SingleOrDefault();
        if (contact.Grant == null) throw new WorkbenchError("联系人已不存在",WorkbenchFailure.NotFound);
        if (contact.Sender == null) throw new WorkbenchError("请先更新申请方工作台，以便按 Agent 移除权限",WorkbenchFailure.Conflict);
        Exec("INSERT INTO fed_denied_sender VALUES($p0,$p1)",contact.Grant,contact.Sender);
        Exec("DELETE FROM fed_return_contact WHERE grant_id=$p0 AND sender=$p1",contact.Grant,contact.Sender);
        string notice = $"@{contact.Provider} 已移除 @{contact.Name} 的协作请求权限。";
        Exec("INSERT INTO fed_event(grant_id,id,kind,request_id,body) VALUES($p0,lower(hex(randomblob(16))),'contact-removed',$p1,$p2)",
            contact.Grant,contact.Sender,notice);
    });

    private void ReceiveContactRemoval(string remote, PeerEvent item)
    {
        // 即使已换绑，权限通知仍属于原 Agent。仅在收到撤权事件时解析节点范围内的发件人编号。
        var agent = Read("SELECT id,topic_id,closed FROM windows",r=>(Id:r.GetString(0),Topic:r.GetString(1),Closed:r.GetBoolean(2)))
            .FirstOrDefault(w=>ScopedContactId(remote,w.Id)==item.RequestId);
        if (agent.Id == null || Exec("INSERT OR IGNORE INTO fed_remote_denial VALUES($p0,$p1)",remote,agent.Id)==0) return;
        string label = Read("SELECT agent||' · '||name FROM fed_remote WHERE id=$p0",r=>r.GetString(0),remote).Single();
        string? id = null;
        if (!agent.Closed && Topic(agent.Topic).Status=="active")
        {
            id = Guid.NewGuid().ToString("N");
            QueueIncoming(id,Window(agent.Id),remote,label,"[协作权限通知] "+item.Text,null,null);
        }
        Mail(agent.Topic,"permission",remote,agent.Id,label,item.Text,taskId:id);
    }

    internal void ContactsPublished(string remote,bool published) =>
        Exec("UPDATE fed_remote SET contacts_published=$p1 WHERE id=$p0 AND contacts_published!=$p1",remote,published);

    internal static bool ReservedContactName(string name) => name.Contains('/') || Guid.TryParseExact(name,"N",out _)
        || name.StartsWith("reply-",StringComparison.Ordinal);

    internal bool ContactNameCollides(AgentWindow caller, ReturnContact contact) =>
        ReservedContactName(contact.Name) || Windows().Any(w=>w.TopicId==caller.TopicId && w.Name.Equals(contact.Name,StringComparison.OrdinalIgnoreCase))
        || RemoteRecipients(caller.TopicId,caller.Id).Any(r=>r.AgentName.Equals(contact.Name,StringComparison.OrdinalIgnoreCase))
        || ReturnContacts(caller.Id).Any(c=>c.Id!=contact.Id && c.Name.Equals(contact.Name,StringComparison.OrdinalIgnoreCase));

    private InboxItem SendContactNotice(string grant, AgentWindow caller, ReturnContact contact, string body, string? document,string[] files,string? hint)
    {
        if (document != null || files.Length != 0 || hint != null || body.Length > 8000)
            throw new WorkbenchError("联系人通知支持 8000 字以内的文本，回送任务文档请关联原 MSG_ID");
        string id = Guid.NewGuid().ToString("N");
        Atomic(() =>
        {
            Caller(grant);
            if (DedicatedReader(caller.Id)) throw new WorkbenchError("专用审阅终端只能回信给当前任务",WorkbenchFailure.Forbidden);
            if (Exec("""
                INSERT INTO fed_event(grant_id,id,kind,request_id,body)
                SELECT g.id,$p2,'contact',c.key,$p3 FROM fed_return_contact c JOIN fed_grant g ON g.id=c.grant_id
                JOIN fed_share s ON s.window_id=g.window_id WHERE g.window_id=$p0 AND c.id=$p1 AND g.status='approved' AND g.paused=0
                """,caller.Id,contact.Id,id,body)!=1) throw new WorkbenchError("回信联系人已失效或暂停",WorkbenchFailure.Forbidden);
            Mail(caller.TopicId,"reply-out",caller.Id,contact.Id,contact.Name+" · "+contact.NodeName,body);
        });
        return new(id,contact.Id,caller.Name,body,[],null,null,"queued");
    }

    private void ReceiveContactNotice(string remote, PeerEvent item)
    {
        var address = Read("""
            SELECT b.agent,b.topic,r.name,r.agent FROM fed_contact_binding b
            JOIN fed_remote r ON r.id=b.remote JOIN windows w ON w.id=b.agent AND w.closed=0 AND w.topic_id=b.topic
            JOIN topics t ON t.id=b.topic AND t.status='active' AND t.leader_window_id=b.agent
            WHERE b.remote=$p0 AND b.key=$p1
              AND NOT EXISTS(SELECT 1 FROM fed_remote_denial d WHERE d.remote=b.remote AND d.agent=b.agent)
            """,r=>(Agent:r.GetString(0),Topic:r.GetString(1),Label:r.GetString(3)+" · "+r.GetString(2)),remote,item.RequestId).SingleOrDefault();
        if (address.Agent == null) return;
        string id = Guid.NewGuid().ToString("N");
        QueueIncoming(id,Window(address.Agent),remote,address.Label,"[EXTERNAL_REPLY] "+item.Text,null,null);
        Mail(address.Topic,"reply-in",remote,address.Agent,address.Label,item.Text,taskId:id);
    }
}

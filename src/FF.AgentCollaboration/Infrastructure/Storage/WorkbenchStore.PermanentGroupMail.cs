using System.Text.Json;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Files;

namespace FF.AgentCollaboration.Infrastructure.Storage;

internal sealed partial class WorkbenchStore
{
    private static string GroupAddress(string address) => address.TrimStart('@');

    internal GroupRouteMember? ResolveGroupRecipient(AgentWindow actor,string address)
    {
        string target=GroupAddress(address);
        return GroupRecipients(actor.TopicId,actor.Id)
            .SingleOrDefault(member=>member.Key==target);
    }

    private InboxItem QueueGroupMessage(GroupRouteMember target,ChatOrigin actor,string post,string text,
        string[] paths,string? replyTo=null,bool expectsReply=false,bool? final=null,bool supersede=false)
    {
        var link=Group(target.Link);
        var grant=Grant(link.Id);
        var roster=LocalGroupRoster(link,grant);
        if (!roster.Active || link.RemoteStatus!="approved" || !link.Remote.Active ||
            (actor.Id!=InboxItem.ChatUser && !roster.Members.Any(member=>member.Id==actor.Id)))
            throw new WorkbenchError("当前成员没有此小组的协作权限",WorkbenchFailure.Forbidden);
        string? recipient=target.Window==InboxItem.ChatUser?null:target.Window;
        if (recipient!=null && !link.Remote.Members.Any(member=>member.Id==recipient))
            throw new WorkbenchError("此成员已不参与常驻协作",WorkbenchFailure.Forbidden);
        if (paths.Length>0 && !link.RemoteUpload) throw new WorkbenchError("对端未开放附件接收",WorkbenchFailure.Forbidden);
        text=WorkbenchInput.Text(text,"跨端消息",8000);
        string id=Guid.NewGuid().ToString("N");
        if (supersede)
        {
            Exec("""
                UPDATE fed_group_mail SET delivery='superseded'
                WHERE link=$p0 AND incoming=0 AND sender IS $p1 AND target IS $p2 AND delivery='pending'
                  AND checked=0 AND json_extract(packet,'$.replyTo') IS NULL
                """,link.Id,actor.Id==InboxItem.ChatUser?null:actor.Id,recipient);
            Exec("""
                DELETE FROM fed_send_file WHERE dispatch IN (
                    SELECT id FROM fed_group_mail WHERE link=$p0 AND delivery='superseded'
                )
                """,link.Id);
        }
        var files=QueueGroupFiles(id,post,paths);
        var packet=new GroupPacket(id,actor.Id==InboxItem.ChatUser?null:actor.Id,recipient,text,replyTo,expectsReply,final,files);
        Exec("""
            INSERT INTO fed_group_mail(id,link,post,incoming,packet,target,sender,delivery)
            VALUES($p0,$p1,$p2,0,$p3,$p4,$p5,'pending')
            """,id,link.Id,post,GroupEncode(packet),packet.Recipient,packet.Sender);
        LinkChat(id,post,link.Topic,target.Key,target.Name,true);
        return new(id,target.Key,actor.Name,text,paths,null,null,"queued");
    }

    private GroupFileRef[] QueueGroupFiles(string packet,string post,string[] paths)
    {
        if (paths.Length==0) return [];
        if (paths.Length>20) throw new WorkbenchError("每条消息最多附带 20 个文件");
        var queued=Read("""
            SELECT count(*),coalesce(sum(length(f.content)),0) FROM fed_send_file f
            WHERE f.dispatch IN (
                SELECT id FROM fed_outbox WHERE state IN ('awaiting_user','pending','sending')
                UNION ALL SELECT id FROM fed_group_mail WHERE delivery='pending'
            )
            """,r=>(Count:r.GetInt64(0),Bytes:r.GetInt64(1))).Single();
        var files=new List<GroupFileRef>();
        long size=0;
        foreach(string path in paths.Distinct(ProjectFolders.Comparer))
        {
            string originalName=Path.GetFileName(path);
            string name=SafePeerName(ChatUploadPath(post,path)?originalName[3..]:originalName);
            using var stream=ProjectFileBrowser.OpenVerified(path);
            size+=stream.Length;
            if (size>ChatUploadLimit || size+queued.Bytes>100L*1024*1024 || files.Count+queued.Count>=250)
                throw new WorkbenchError("待发送附件已达到上限");
            var bytes=PeerAttachmentBytes(stream,name,CancellationToken.None).GetAwaiter().GetResult();
            string id=Guid.NewGuid().ToString("N");
            Exec("INSERT INTO fed_send_file VALUES($p0,$p1,$p2,$p3)",id,packet,name,bytes);
            files.Add(new(id,name));
        }
        return files.ToArray();
    }

    internal InboxItem? SendGroupCollaboration(string caller,string address,string text,string[] files,string? document,
        string? hint,bool supersede,string? replyTo,bool? final)
    {
        var actor=Caller(caller);
        string key=GroupAddress(address);
        var target=ResolveGroupRecipient(actor,address);
        bool replyAddress=key.StartsWith("reply-",StringComparison.Ordinal);
        string? reference=replyTo ?? (replyAddress?key[6..]:null);
        if (reference!=null && (reference.Length is not (8 or 32) || !reference.All(char.IsAsciiHexDigit)))
            throw new WorkbenchError("请提供原消息的 8 位或完整编号");
        var original=reference==null?default:Read("""
            SELECT mail.id,mail.link,mail.post,mail.sender,mail.inbox FROM fed_group_mail mail
            JOIN inbox i ON i.id=mail.inbox WHERE mail.incoming=1 AND i.target=$p0 AND mail.inbox GLOB $p1 LIMIT 2
            """,r=>(Id:r.GetString(0),Link:r.GetString(1),Post:r.GetString(2),Sender:Optional(r,3),Inbox:r.GetString(4)),
            actor.Id,WorkbenchInput.Text(reference,"原消息编号",32).ToLowerInvariant()+(reference.Length==8?"*":"")).SingleOrDefault();
        if (original.Id==null && target==null) return null;
        if (original.Id==null && reference!=null) throw new WorkbenchError("找不到对应的跨端来信",WorkbenchFailure.Forbidden);
        if (DedicatedReader(actor.Id)) throw new WorkbenchError("专用审阅终端仅可回复原审阅任务",WorkbenchFailure.Forbidden);
        if (files.Length!=0 && !AttachmentPermission(actor.Id))
            throw new WorkbenchError("请由用户发送附件，或先为此 Agent 授权文件发送");
        var topic=Topic(actor.TopicId);
        string[] paths=files.Select(path=>ProjectFolders.Attachment(actor.Project,path)).ToArray();
        if (document!=null) paths=[ProjectMarkdown(actor.Project,topic.Folder,document),..paths];
        var origin=ChatOrigin.From(actor);
        string post=Guid.NewGuid().ToString("N");
        InboxItem? result=null;
        Atomic(()=>{
            if (original.Id!=null)
            {
                var task=ReceivedTaskFor(actor.Id,original.Inbox);
                var link=Group(original.Link);
                var replyTarget=GroupMemberTarget(link,original.Sender);
                if ((!replyAddress && target?.Key!=replyTarget.Key) || (replyAddress && key[6..]!=original.Inbox))
                    throw new WorkbenchError("回信目标须对应原来信",WorkbenchFailure.Forbidden);
                SaveChat(post,origin,text,paths,original.Post);
                if (task.ExpectsReply) RecordTaskReply(task,final??true);
                result=QueueGroupMessage(replyTarget,origin,post,text,paths,original.Id,final:final??true);
            }
            else
            {
                SaveChat(post,origin,text,paths);
                result=QueueGroupMessage(target!,origin,post,text,paths,expectsReply:hint!=null,supersede:supersede);
            }
        });
        return result;
    }

    private static GroupRouteMember GroupMemberTarget(GroupLink link,string? id)
    {
        var member=id==null?null:link.Remote.Members.FirstOrDefault(item=>item.Id==id)
            ?? throw new FederationException(403,"group_member_removed");
        return new(ScopedContactId(link.Id,id??InboxItem.ChatUser),link.Id,id??InboxItem.ChatUser,
            member?.Name??link.User+" · 用户",link.Remote.Name,link.User,member?.Engine,id!=null && id==link.Remote.Leader,
            link.Connected && (member?.Online??true));
    }

    private void RecheckGroupInbox(GroupLink link,PeerGrant grant)
    {
        var roster=LocalGroupRoster(link,grant);
        if (!roster.Active || !link.Remote.Active || link.RemoteStatus!="approved")
        {
            WithdrawPeerInbox(link.Id);
            return;
        }
        Exec("""
            UPDATE inbox SET acknowledged=1,wake_requested=0 WHERE state='pending' AND id IN (
                SELECT m.inbox FROM fed_group_mail m WHERE m.link=$p0 AND m.incoming=1 AND
                (m.target NOT IN (SELECT value FROM json_each($p1))
                 OR (m.sender IS NOT NULL AND m.sender NOT IN (SELECT value FROM json_each($p2))))
            )
            """,link.Id,GroupEncode(roster.Members.Select(member=>member.Id)),GroupEncode(link.Remote.Members.Select(member=>member.Id)));
    }

    private GroupReceipt ReceiveGroupMessage(GroupLink link,GroupPacket packet)
    {
        string id=WorkbenchInput.Id(packet.Id);
        var prior=Read("""
            SELECT link,packet FROM fed_group_mail WHERE id=$p0 AND incoming=1
            """,r=>(Link:r.GetString(0),Packet:r.GetString(1)),id).SingleOrDefault();
        string encoded=GroupEncode(packet);
        if (prior.Link!=null)
        {
            if (prior.Link!=link.Id || prior.Packet!=encoded) throw new FederationException(409,"message_id_conflict");
            return GroupReceipts(link.Id,[id]).Single();
        }
        var grant=Grant(link.Id);
        if (GroupPacketDenial(link,grant,packet) is string denied) return new(id,denied,null);
        string text=WorkbenchInput.Text(packet.Text,"跨端消息",8000);
        string? quote=null;
        if (packet.ReplyTo!=null)
        {
            quote=Read("""
                SELECT post FROM fed_group_mail WHERE link=$p0 AND id=$p1 AND incoming=0
                AND sender IS $p2 AND target IS $p3
                """,r=>r.GetString(0),link.Id,WorkbenchInput.Id(packet.ReplyTo),packet.Recipient,packet.Sender).SingleOrDefault();
            if (quote==null) return new(id,"blocked",null);
        }
        long bytes=0;
        string[] paths=packet.Files.Select(file=>{
            var upload=Read("""
                SELECT f.name,f.size FROM fed_file f JOIN fed_group_upload p ON p.file=f.id
                WHERE f.owner=$p0 AND f.id=$p1 AND p.message=$p2 AND p.sender IS $p3 AND p.recipient IS $p4
                """,r=>(Name:r.GetString(0),Bytes:r.GetInt64(1)),link.Id,WorkbenchInput.Id(file.Id),
                id,packet.Sender,packet.Recipient).SingleOrDefault();
            if (upload.Name!=file.Name || (bytes+=upload.Bytes)>ChatUploadLimit)
                throw new FederationException(409,"invalid_upload");
            return BindPeerFile(link.Id,file.Id,id);
        }).ToArray();
        string? inbox=packet.Recipient!=null?Guid.NewGuid().ToString("N"):null;
        var from=GroupMemberTarget(link,packet.Sender);
        var topic=Topic(link.Topic);
        SaveChat(id,new(from.Key,from.Name+" · "+link.Remote.Name+" · "+link.User,topic.Id,topic.Project),text,[],quote);
        if (inbox!=null)
        {
            var window=Window(packet.Recipient!);
            string readMode=grant.PermissionMode=="read_only"
                ? "[EXTERNAL_COLLAB_POLICY: READ_ONLY | DO NOT EDIT/WRITE/COMMIT/PUSH ANY FILES] "
                : "[EXTERNAL_COLLAB_POLICY: READ_WRITE] ";
            string reply="请用 send_to_agent 向 @reply-"+inbox+" 回信，replyTo 填本条 MSG_ID。";
            Exec("""
                INSERT INTO fed_request(grant_id,id,body,permission,expects_reply,sender)
                VALUES($p0,$p1,$p2,$p3,$p4,$p5)
                """,link.Id,id,text,grant.PermissionMode,packet.ExpectsReply,packet.Sender);
            Exec("INSERT INTO fed_incoming(local,grant_id,request) VALUES($p0,$p1,$p2)",inbox,link.Id,id);
            QueueIncoming(inbox,window,"peer-mailbox",from.Name+" · "+link.Remote.Name,readMode+text+
                (packet.ReplyTo==null && !packet.ExpectsReply?"\nGROUP_REPLY: "+reply:""),null,packet.ExpectsReply?reply:null);
            Exec("UPDATE inbox SET files=$p1,work_state='queued' WHERE id=$p0",inbox,JsonSerializer.Serialize(paths));
            LinkChat(inbox,id,topic.Id,window.Id,window.Name);
        }
        if (packet.ReplyTo==null) ChargePeerQuota(grant,link.User);
        Exec("""
            INSERT INTO fed_group_mail(id,link,post,incoming,packet,target,sender,inbox,delivery)
            VALUES($p0,$p1,$p0,1,$p2,$p3,$p4,$p5,$p6)
            """,id,link.Id,encoded,packet.Recipient,packet.Sender,inbox,inbox==null?"delivered":"accepted");
        return new(id,inbox==null?"delivered":"accepted",inbox==null?null:"queued");
    }

    private GroupReceipt[] GroupReceipts(string link,string[] ids)
    {
        if (ids is not {Length:<=24}) throw new FederationException(400,"invalid_group_receipts");
        return Read("""
            SELECT m.id,CASE WHEN i.acknowledged THEN 'ignored'
                WHEN i.id IS NOT NULL AND i.state NOT IN ('pending','writing') THEN i.state ELSE m.delivery END,i.work_state
            FROM fed_group_mail m LEFT JOIN inbox i ON i.id=m.inbox
            WHERE m.link=$p0 AND m.incoming=1 AND m.id IN (SELECT value FROM json_each($p1))
            """,r=>new GroupReceipt(r.GetString(0),r.GetString(1),Optional(r,2)),link,GroupEncode(ids.Select(WorkbenchInput.Id))).ToArray();
    }

    private string? GroupPacketDenial(GroupLink link,PeerGrant grant,GroupPacket packet)
    {
        if (!GroupCanReceive(link,grant,packet.Sender,packet.Recipient)) return "blocked";
        if (packet.Files is not {Length:<=20} || packet.Files.Any(file=>file==null) ||
            packet.Files.Select(file=>file.Id).Distinct().Count()!=packet.Files.Length)
            throw new FederationException(400,"invalid_group_files");
        if (packet.Files.Length>0 && !grant.AllowUpload) return "upload_disabled";
        if (grant.QuotaLimit is int limit && grant.Used>=limit && packet.ReplyTo==null) return "quota_exceeded";
        return null;
    }

    private bool GroupCanReceive(GroupLink link,PeerGrant grant,string? sender,string? recipient)
    {
        var roster=LocalGroupRoster(link,grant);
        return roster.Active && link.RemoteStatus=="approved" && link.Remote.Active &&
            (sender==null || link.Remote.Members.Any(member=>member.Id==sender)) &&
            (recipient==null || roster.Members.Any(member=>member.Id==recipient));
    }

    private string? OutboundGroupDenial(GroupLink link,GroupRoster local,GroupPacket packet)
    {
        if (!local.Active || link.RemoteStatus!="approved" || !link.Remote.Active ||
            packet.Sender!=null && !local.Members.Any(member=>member.Id==packet.Sender) ||
            packet.Recipient!=null && !link.Remote.Members.Any(member=>member.Id==packet.Recipient)) return "blocked";
        return packet.Files.Length>0 && !link.RemoteUpload ? "upload_disabled" : null;
    }

    private GroupPacket? GroupQueued(GroupLink link,GroupRoster local)
    {
        var queued=Read("""
            SELECT packet FROM fed_group_mail WHERE link=$p0 AND incoming=0 AND delivery='pending' ORDER BY rowid LIMIT 8
            """,r=>GroupDecode<GroupPacket>(r.GetString(0)),link.Id);
        foreach(var packet in queued)
        {
            if (OutboundGroupDenial(link,local,packet) is string denied)
            {
                ApplyGroupReceipts(link.Id,[new(packet.Id,denied,null)]);
                continue;
            }
            Exec("UPDATE fed_group_mail SET checked=unixepoch() WHERE id=$p0",packet.Id);
            return packet;
        }
        return null;
    }

    private string[] GroupTracking(string link)
    {
        var ids=Read("""
            SELECT id FROM fed_group_mail WHERE link=$p0 AND incoming=0
            AND (delivery IN ('accepted','delivery_uncertain') OR
                 (delivery='delivered' AND work IN ('queued','working','need_info')))
            ORDER BY checked,rowid LIMIT 12
            """,r=>r.GetString(0),link).ToArray();
        if (ids.Length>0) Exec("""
            UPDATE fed_group_mail SET checked=unixepoch() WHERE id IN (SELECT value FROM json_each($p0))
            """,GroupEncode(ids));
        return ids;
    }

    private bool ApplyGroupReceipts(string link,GroupReceipt[] receipts)
    {
        if (receipts is not {Length:<=25}) throw new FederationException(400,"invalid_group_receipts");
        bool changed=false;
        foreach(var receipt in receipts)
        {
            if (receipt==null || receipt.Delivery is not ("accepted" or "delivered" or "ignored" or "delivery_uncertain" or
                 "blocked" or "upload_disabled" or "quota_exceeded" or "attachment_failed") ||
                receipt.Work is not (null or "queued" or "working" or "need_info" or "completed" or "replied"))
                throw new FederationException(400,"invalid_group_receipt");
            var id=WorkbenchInput.Id(receipt.Id);
            if (Exec("""
                UPDATE fed_group_mail SET delivery=$p2,work=$p3 WHERE id=$p0 AND link=$p1 AND incoming=0
                AND delivery IN ('pending','accepted','delivered','delivery_uncertain')
                AND (delivery!='delivered' OR $p2='delivered')
                AND (work IS NULL OR work NOT IN ('completed','replied') OR work IS $p3)
                AND (delivery!=$p2 OR work IS NOT $p3)
                """,id,link,receipt.Delivery,receipt.Work)>0)
            {
                changed=true;
                Exec("DELETE FROM fed_send_file WHERE dispatch=$p0",id);
            }
        }
        return changed;
    }

    internal void CheckGroupUpload(string id,string token,string node,GroupFileRoute route)
    {
        var link=AuthorizeGroup(id,token,node);
        CheckGroupIncomingFile(link,route);
    }

    private void CheckGroupIncomingFile(GroupLink link,GroupFileRoute route)
    {
        var grant=Grant(link.Id);
        if (!grant.AllowUpload || !GroupCanReceive(link,grant,route.Sender,route.Recipient))
            throw new FederationException(403,"upload_disabled");
    }

    internal void StageGroupUpload(string id,GroupFileRoute route,string file) => Atomic(()=>{
        CheckGroupIncomingFile(Group(id),route);
        if (Exec("INSERT OR IGNORE INTO fed_group_upload VALUES($p0,$p1,$p2,$p3)",file,route.Message,route.Sender,route.Recipient)==0 &&
            !Read("""
                SELECT 1 FROM fed_group_upload WHERE file=$p0 AND message=$p1 AND sender IS $p2 AND recipient IS $p3
                """,r=>true,file,route.Message,route.Sender,route.Recipient).Any())
            throw new FederationException(409,"upload_id_conflict");
    });

    internal (string Name,byte[] Bytes) GroupOutboundFile(string id,string token,string node,string file)
    {
        var link=AuthorizeGroup(id,token,node);
        var item=Read("""
            SELECT f.name,f.content,m.packet FROM fed_send_file f JOIN fed_group_mail m ON m.id=f.dispatch
            WHERE f.id=$p0 AND m.link=$p1 AND m.incoming=0 AND m.delivery='pending' AND m.checked>0
            """,r=>(Name:r.GetString(0),Bytes:(byte[])r[1],Packet:GroupDecode<GroupPacket>(r.GetString(2))),WorkbenchInput.Id(file),id).SingleOrDefault();
        if (item.Name==null) throw new FederationException(404,"file_expired");
        if (OutboundGroupDenial(link,LocalGroupRoster(link,Grant(link.Id)),item.Packet!)!=null)
            throw new FederationException(403,"upload_disabled");
        return (item.Name,item.Bytes);
    }
}

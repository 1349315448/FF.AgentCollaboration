using FF.AgentCollaboration.Domain;

namespace FF.AgentCollaboration.Infrastructure.Storage;

internal sealed record GroupPeerConnection(RemoteConnection Transport,GroupApplication? Application,bool Revoking);
internal sealed record GroupPush(GroupExchange Exchange,OutgoingAttachment[] Files);

internal sealed partial class WorkbenchStore
{
    internal List<string> ActiveGroupConnections() => Read("""
        SELECT l.id FROM fed_group_link l JOIN fed_grant g ON g.id=l.id
        WHERE l.endpoint IS NOT NULL AND l.peer_status NOT IN ('revoked','rejected')
        AND (g.status='approved' OR (g.status='revoked' AND l.claim IS NOT NULL))
        """,r=>r.GetString(0));

    internal GroupPeerConnection? GroupConnection(string id)
    {
        var link=Group(id);
        var grant=Grant(id);
        if (link.Endpoint==null || link.RemoteStatus is "revoked" or "rejected") return null;
        var transport=new RemoteConnection(id,link.InvitedWindow,link.Endpoint,link.Pin!,link.Node,link.Share,link.Claim!,
            link.Token,link.RemoteStatus,grant.Purpose,0,false);
        var application=link.Share==null?null:new GroupApplication(id,link.Claim!,grant.Purpose,LocalGroupRoster(link,grant),
            link.SuggestedMember,link.MemberMode=="all" && link.SuggestedMember==null);
        return new(transport,application,grant.Status=="revoked");
    }

    internal bool ReceiveGroupSession(string id,GroupSession session)
    {
        bool changed=false;
        Atomic(()=>{
            var link=Group(id);
            if (session==null || session.Id!=id || session.Node!=link.Node ||
                session.Status is not ("pending" or "approved" or "rejected" or "revoked"))
                throw new FederationException(409,"invalid_group_session");
            if (session.Token!=null && (!FederationPolicy.IsSecret(session.Token) || session.Status!="approved"))
                throw new FederationException(409,"invalid_group_session");
            if (link.Token==null && session.Token==null && session.Status=="approved")
                throw new FederationException(409,"invalid_group_session");
            changed=SetRemoteGroup(link,session.Roster,FederationPolicy.Name(session.User),session.Status,session.Quota,
                session.AllowUpload,session.Token);
            if (link.RemoteStatus is "applying" or "pending" && session.Status is "approved" or "rejected")
                Mail(link.Topic,"permission",link.Id,InboxItem.ChatUser,session.User,
                    $"{session.User} 已{(session.Status=="approved"?"同意":"拒绝")}你与「{session.Roster.Name}」的小组协作申请。");
        });
        return changed;
    }

    internal bool GroupConnectFailed(string id,bool rejected) => AtomicResult(()=>{
        if (rejected)
        {
            StopGroupLink(id,"rejected");
            return Exec("UPDATE fed_group_link SET peer_status='rejected',seen=0 WHERE id=$p0 AND peer_status!='rejected'",id)>0;
        }
        return Exec("UPDATE fed_group_link SET seen=0 WHERE id=$p0 AND seen!=0",id)>0;
    });

    private bool AtomicResult(Func<bool> action)
    {
        bool result=false;
        Atomic(()=>result=action());
        return result;
    }

    internal bool ExpireGroupConnections() => Exec("UPDATE fed_group_link SET seen=0 WHERE seen!=0 AND seen<=unixepoch()-12")>0;

    internal bool HasHostedGroup(string window) =>
        Read("""
            SELECT 1 FROM fed_group_link l JOIN fed_grant g ON g.id=l.id
            WHERE l.endpoint IS NULL AND (l.invited_window=$p0 OR g.window_id=$p0) AND g.status='approved' LIMIT 1
            """,r=>true,window).Any();

    internal GroupPush PrepareGroupPush(string id,string[] track,GroupReceipt? next)
    {
        var link=Group(id);
        var grant=Grant(id);
        var roster=LocalGroupRoster(link,grant);
        bool ready=roster.Active && link.Remote.Active && link.RemoteStatus=="approved";
        GroupPacket? packet=ready?GroupQueued(link,roster):null;
        var receipts=GroupReceipts(id,track);
        if (next!=null) receipts=[..receipts,next];
        var files=packet==null?[]:OutgoingAttachments(packet.Id).ToArray();
        return new(new(roster,PeerQuota.From(grant),grant.AllowUpload,grant.Status=="revoked",
            packet,GroupTracking(id),receipts),files);
    }

    internal GroupExchangeResult ExchangeGroup(string id,string token,string node,string user,GroupExchange pull,out bool changed)
    {
        GroupExchangeResult? result=null;
        bool updated=false;
        Atomic(()=>{
            var link=AuthorizeGroup(id,token,node);
            var grant=Grant(id);
            if (pull.Revoke && grant.Status=="approved") { StopGroupLink(id,"revoked"); updated=true; }
            if (grant.Status!="approved" || pull.Revoke)
            {
                result=new(GroupStatus(link,Grant(id)),null,[],[]);
                return;
            }
            if (pull.Roster==null) throw new FederationException(400,"invalid_group_roster");
            updated=SetRemoteGroup(link,pull.Roster,user,"approved",pull.Quota,pull.AllowUpload,null);
            link=Group(id);
            updated|=ApplyGroupReceipts(id,pull.Receipts);
            var receipts=GroupReceipts(id,pull.Track);
            if (pull.Message!=null)
            {
                bool known=Read("SELECT 1 FROM fed_group_mail WHERE id=$p0 AND incoming=1",r=>true,pull.Message.Id).Any();
                var accepted=ReceiveGroupMessage(link,pull.Message);
                updated|=!known && accepted.Delivery is "accepted" or "delivered";
                receipts=[..receipts.Where(receipt=>receipt.Id!=accepted.Id),accepted];
            }
            var roster=LocalGroupRoster(link,grant);
            result=new(GroupStatus(link,Grant(id)),roster.Active && link.Remote.Active?GroupQueued(link,roster):null,GroupTracking(id),receipts);
        });
        changed=updated;
        return result!;
    }

    internal bool ReceiveGroupReceipts(string id,GroupReceipt[] receipts) =>
        AtomicResult(()=>ApplyGroupReceipts(id,receipts));

    internal GroupReceipt ReceiveGroupPull(string id,GroupPacket packet)
    {
        GroupReceipt? receipt=null;
        Atomic(()=>receipt=ReceiveGroupMessage(Group(id),packet));
        return receipt!;
    }

    internal GroupReceipt? CheckGroupPull(string id,GroupPacket packet)
    {
        var link=Group(id);
        var saved=GroupReceipts(id,[packet.Id]).SingleOrDefault();
        if (saved!=null) return saved;
        return GroupPacketDenial(link,Grant(id),packet) is string denied?new(packet.Id,denied,null):null;
    }

    internal void CheckGroupPullFile(string id,GroupFileRoute route) => CheckGroupIncomingFile(Group(id),route);

    internal bool GroupTerminalAllowed(InboxItem item)
    {
        var link=Read("""
            SELECT m.link,m.sender FROM fed_group_mail m WHERE m.inbox=$p0 AND m.incoming=1
            """,r=>(Id:r.GetString(0),Sender:Optional(r,1)),item.Id).SingleOrDefault();
        if (link.Id==null) return true;
        var binding=Group(link.Id);
        var roster=LocalGroupRoster(binding,Grant(binding.Id));
        return roster.Active && binding.Remote.Active && binding.RemoteStatus=="approved" &&
            roster.Members.Any(member=>member.Id==item.Target) &&
            (link.Sender==null || binding.Remote.Members.Any(member=>member.Id==link.Sender));
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using FF.AgentCollaboration.Domain;

namespace FF.AgentCollaboration.Infrastructure.Storage;

internal sealed partial class WorkbenchStore
{
    private sealed record GroupLink(string Id, string Topic, string Node, string InvitedWindow, string User,
        GroupRoster Remote, string RemoteStatus, string[] Members, PeerQuota? RemoteQuota, bool RemoteUpload,
        string? Endpoint, string? Pin, string? Share, string? Claim, string? Token, bool Connected, bool ReviewRequired,string MemberMode,string? SuggestedMember,long? ExpiresAt);

    private static readonly JsonSerializerOptions GroupJson = new(JsonSerializerDefaults.Web);

    private void InitializeGroupCollaboration()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS fed_group_link(
                id TEXT PRIMARY KEY, topic TEXT NOT NULL, invited_window TEXT NOT NULL, peer_node TEXT NOT NULL,
                peer_user TEXT NOT NULL DEFAULT '', roster TEXT NOT NULL, peer_status TEXT NOT NULL,
                members TEXT NOT NULL, peer_quota TEXT, peer_upload INTEGER NOT NULL DEFAULT 0,
                endpoint TEXT, pin TEXT, share TEXT, claim TEXT, token TEXT, seen INTEGER NOT NULL DEFAULT 0,
                review_required INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS fed_group_topic ON fed_group_link(topic,peer_node);
            CREATE TABLE IF NOT EXISTS fed_group_mail(
                id TEXT PRIMARY KEY, link TEXT NOT NULL, post TEXT NOT NULL, incoming INTEGER NOT NULL,
                packet TEXT NOT NULL, target TEXT, sender TEXT, inbox TEXT, delivery TEXT NOT NULL,
                work TEXT, checked INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS fed_group_mail_link ON fed_group_mail(link,incoming,delivery,checked);
            CREATE UNIQUE INDEX IF NOT EXISTS fed_group_mail_inbox ON fed_group_mail(inbox) WHERE inbox IS NOT NULL;
            CREATE TABLE IF NOT EXISTS fed_group_upload(
                file TEXT PRIMARY KEY, message TEXT NOT NULL, sender TEXT, recipient TEXT
            );
            CREATE TRIGGER IF NOT EXISTS fed_groups_review_after_leader_change AFTER UPDATE OF leader_window_id ON topics
                WHEN OLD.leader_window_id IS NOT NEW.leader_window_id
            BEGIN
                UPDATE fed_group_link SET review_required=1 WHERE topic=NEW.id;
                UPDATE inbox SET wake_requested=0,acknowledged=1 WHERE state='pending' AND id IN (
                    SELECT m.inbox FROM fed_group_mail m JOIN fed_group_link l ON l.id=m.link
                    WHERE l.topic=NEW.id AND m.incoming=1
                );
            END;
            UPDATE fed_group_link SET seen=0;
            """);
        var columns=Read("PRAGMA table_info(fed_group_link)",r=>r.GetString(1));
        if (!columns.Contains("expires_at")) Exec("ALTER TABLE fed_group_link ADD COLUMN expires_at INTEGER");
        if (!columns.Contains("member_mode")) Exec("ALTER TABLE fed_group_link ADD COLUMN member_mode TEXT NOT NULL DEFAULT 'legacy'");
        if (!columns.Contains("suggested_member")) Exec("ALTER TABLE fed_group_link ADD COLUMN suggested_member TEXT");
        if (!Read("PRAGMA table_info(fed_grant)",r=>r.GetString(1)).Contains("kind"))
            Exec("""
                ALTER TABLE fed_grant ADD COLUMN kind TEXT NOT NULL DEFAULT 'agent';
                DROP INDEX fed_open_peer;
                CREATE UNIQUE INDEX fed_open_peer ON fed_grant(window_id,node)
                    WHERE kind='agent' AND status IN ('pending','approved');
                """);
    }

    private const string GroupFields = """
        id,topic,peer_node,invited_window,peer_user,roster,peer_status,members,peer_quota,peer_upload,
        endpoint,pin,share,claim,token,seen>unixepoch()-12,review_required,member_mode,suggested_member,expires_at
        """;

    private static GroupLink ReadGroup(Microsoft.Data.Sqlite.SqliteDataReader row) =>
        new(row.GetString(0),row.GetString(1),row.GetString(2),row.GetString(3),row.GetString(4),
            GroupDecode<GroupRoster>(row.GetString(5)),row.GetString(6),GroupDecode<string[]>(row.GetString(7)),
            row.IsDBNull(8)?null:GroupDecode<PeerQuota>(row.GetString(8)),row.GetBoolean(9),
            Optional(row,10),Optional(row,11),Optional(row,12),Optional(row,13),Optional(row,14),row.GetBoolean(15),row.GetBoolean(16),row.GetString(17),Optional(row,18),row.IsDBNull(19)?null:row.GetInt64(19));

    private static string GroupEncode<T>(T value) => JsonSerializer.Serialize(value,GroupJson);
    private static T GroupDecode<T>(string value) => JsonSerializer.Deserialize<T>(value,GroupJson)!;
    private GroupLink Group(string id) => Read($"SELECT {GroupFields} FROM fed_group_link WHERE id=$p0",ReadGroup,WorkbenchInput.Id(id))
        .SingleOrDefault() ?? throw new FederationException(404,"unknown_group_link");

    private GroupRoster LocalGroupRoster(GroupLink link,PeerGrant grant)
    {
        var topic = Topic(link.Topic);
        string leader = topic.LeaderWindowId ?? grant.WindowId;
        var members = Windows().Where(window=>window.TopicId==topic.Id && (link.MemberMode=="all" || link.Members.Contains(window.Id)) && !DedicatedReader(window.Id))
            .Select(window=>new GroupMember(window.Id,window.Name,window.Engine,window.Online)).ToArray();
        bool active = topic.Status=="active" && topic.LeaderWindowId==grant.WindowId && grant.Status=="approved" && !grant.Paused && !link.ReviewRequired
            && (link.ExpiresAt==null || link.ExpiresAt>DateTimeOffset.UtcNow.ToUnixTimeSeconds()) && members.Length is >0 and <=50;
        return new(topic.Id,topic.Name,leader,members.Length<=50?members:[],active);
    }

    private string[] SelectGroupMembers(AgentWindow leader,string[] members,string? mode=null)
    {
        if (members is not { Length: <= 50 }) throw new WorkbenchError("请选择本组的协作成员，最多 50 位");
        if (mode=="all")
        {
            if (Windows().Count(w=>w.TopicId==leader.TopicId && !DedicatedReader(w.Id))>50)
                throw new WorkbenchError("单次合作最多 50 位成员，请指定参与成员");
            return [];
        }
        if (mode is not (null or "legacy" or "selected")) throw new WorkbenchError("参与范围无效");
        var chosen=members.Select(WorkbenchInput.Id);
        var ids=(mode=="selected"?chosen:chosen.Append(leader.Id)).Distinct().ToArray();
        if (ids.Length is 0 or >50) throw new WorkbenchError("请选择 1 至 50 位参与成员");
        foreach (string id in ids)
        {
            if (Window(id).TopicId!=leader.TopicId || DedicatedReader(id))
                throw new WorkbenchError("只能选择本组内可协作的 Agent",WorkbenchFailure.Forbidden);
        }
        return ids;
    }

    private static void CheckRemoteRoster(GroupRoster roster)
    {
        if (roster is null || roster.Members is not {Length:<=50} || roster.Members.Any(member=>member==null))
            throw new FederationException(400,"invalid_group_roster");
        WorkbenchInput.Id(roster.Topic);
        WorkbenchInput.Id(roster.Leader);
        WorkbenchInput.Text(roster.Name,"小组名称",100);
        if (roster.Members.Select(member=>WorkbenchInput.Id(member.Id)).Distinct().Count()!=roster.Members.Length)
            throw new FederationException(400,"invalid_group_roster");
        foreach(var member in roster.Members)
        {
            WorkbenchInput.AgentName(member.Name);
            WorkbenchInput.Text(member.Engine,"引擎",32);
        }
        if (roster.Active && roster.Members.Length==0)
            throw new FederationException(400,"invalid_group_roster");
    }

    internal string StartGroupCollaboration(string topic,string endpoint,string pin,string node,string window,string token,
        string purpose,GroupSelection selection,string? remoteTopic)
    {
        string id=Guid.NewGuid().ToString("N");
        Atomic(()=>{
            var leader=ExternalLeader(topic);
            if (node==FederationNode().NodeId) throw new WorkbenchError("请选择另一台工作台的小组",WorkbenchFailure.Conflict);
            var existing=Read("""
                SELECT l.id,l.pin FROM fed_group_link l JOIN fed_grant g ON g.id=l.id
                WHERE l.topic=$p0 AND l.peer_node=$p1 AND l.endpoint IS NOT NULL
                  AND (l.invited_window=$p2 OR json_extract(l.roster,'$.topic')=$p3)
                  AND g.status='approved' AND l.peer_status NOT IN ('revoked','rejected')
                """,r=>(Id:r.GetString(0),Pin:r.GetString(1)),topic,node,window,remoteTopic).SingleOrDefault();
            if (existing.Id!=null)
            {
                if (!string.Equals(existing.Pin,pin,StringComparison.OrdinalIgnoreCase))
                    throw new FederationException(409,"certificate_changed");
                id=existing.Id;
                Exec("""
                    UPDATE fed_group_link SET endpoint=$p1,share=CASE WHEN peer_status='applying' THEN $p2 ELSE share END,
                      seen=0 WHERE id=$p0
                    """,id,endpoint,token);
                return;
            }
            var members=SelectGroupMembers(leader,selection.Members,selection.MemberMode);
            CheckGroupDuplicate(topic,node,remoteTopic,window);
            CheckPeerPolicy(selection.Policy,"read_write");
            Exec("""
                INSERT INTO fed_grant(id,window_id,node,name,ip,purpose,status,claim_hash,kind)
                VALUES($p0,$p1,$p2,'','',$p3,'pending',$p4,'group')
                """,id,leader.Id,node,purpose,Hash(NewGrant()));
            SavePeerPolicy(id,selection.Policy);
            Exec("""
                INSERT INTO fed_group_link(id,topic,invited_window,peer_node,roster,peer_status,members,endpoint,pin,share,claim,member_mode,suggested_member,expires_at)
                VALUES($p0,$p1,$p2,$p3,$p4,'applying',$p5,$p6,$p7,$p8,$p9,$p10,$p11,$p12)
                """,id,topic,window,node,GroupEncode(new GroupRoster(remoteTopic??"", "",window,[],false)),GroupEncode(members),endpoint,pin,token,NewGrant(),selection.MemberMode??"legacy",
                selection.SuggestedMember==null?null:WorkbenchInput.Id(selection.SuggestedMember),selection.ExpiresAt);
        });
        return id;
    }

    private void CheckGroupDuplicate(string topic,string node,string? remoteTopic,string window,string? except=null)
    {
        if (Read("""
            SELECT 1 FROM fed_group_link l JOIN fed_grant g ON g.id=l.id
            WHERE l.topic=$p0 AND l.peer_node=$p1 AND l.id IS NOT $p4 AND g.status IN ('pending','approved')
              AND l.peer_status NOT IN ('revoked','rejected') AND
              (($p2 IS NULL AND l.invited_window=$p3) OR json_extract(l.roster,'$.topic')=$p2)
            LIMIT 1
            """,r=>true,topic,node,remoteTopic,window,except).Any())
            throw new FederationException(409,"group_link_exists");
    }

    internal GroupSession ApplyGroupCollaboration(string share,string node,string user,string ip,GroupApplication input)
    {
        string id=WorkbenchInput.Id(input.Id);
        if (!FederationPolicy.IsSecret(input.Claim) || node==FederationNode().NodeId)
            throw new FederationException(403,"invalid_claim");
        string purpose=WorkbenchInput.Text(input.Purpose,"申请用途",300);
        CheckRemoteRoster(input.Roster);
        Atomic(()=>{
            string window=Read("""
                SELECT s.window_id FROM fed_share s JOIN windows w ON w.id=s.window_id AND w.closed=0
                JOIN topics t ON t.id=w.topic_id AND t.leader_window_id=w.id AND t.status='active' WHERE s.token=$p0
                """,r=>r.GetString(0),share).SingleOrDefault() ?? throw new FederationException(403,"invalid_share");
            var leader=Window(window);
            if (DedicatedReader(leader.Id)) throw new FederationException(403,"group_reader_unavailable");
            var prior=Read("SELECT 1 FROM fed_group_link WHERE id=$p0",r=>true,id).Any();
            if (prior)
            {
                var accepted=Group(id);
                if (accepted.Topic!=leader.TopicId || accepted.Remote.Topic!=input.Roster.Topic)
                    throw new FederationException(409,"group_link_conflict");
                GroupClaimGrant(id,input.Claim,node);
                return;
            }
            CheckGroupDuplicate(leader.TopicId,node,input.Roster.Topic,input.Roster.Leader);
            Exec("""
                INSERT INTO fed_grant(id,window_id,node,name,ip,purpose,status,claim_hash,kind)
                VALUES($p0,$p1,$p2,$p3,$p4,$p5,'pending',$p6,'group')
                """,id,leader.Id,node,user,ip,purpose,Hash(input.Claim));
            Exec("""
                INSERT INTO fed_group_link(id,topic,invited_window,peer_node,peer_user,roster,peer_status,members,suggested_member)
                VALUES($p0,$p1,$p2,$p3,$p4,$p5,'approved','[]',$p6)
                """,id,leader.TopicId,window,node,user,GroupEncode(input.Roster),input.SuggestedMember==null?null:WorkbenchInput.Id(input.SuggestedMember));
        });
        return ClaimGroupCollaboration(id,input.Claim,node);
    }

    private string? GroupClaimGrant(string id,string claim,string node)
    {
        var matches=Read("""
            SELECT g.salt FROM fed_grant g JOIN fed_group_link l ON l.id=g.id
            WHERE g.id=$p0 AND g.claim_hash=$p1 AND g.node=$p2 AND l.endpoint IS NULL
            """,r=>Optional(r,0),WorkbenchInput.Id(id),Hash(claim),node);
        if (matches.Count!=1) throw new FederationException(403,"invalid_claim");
        return matches[0];
    }

    internal GroupSession ClaimGroupCollaboration(string id,string claim,string node,bool withdraw=false)
    {
        GroupSession? result=null;
        Atomic(()=>{
            string? salt=GroupClaimGrant(id,claim,node);
            var grant=Grant(id);
            if (withdraw && grant.Status is "pending" or "approved")
            {
                StopGroupLink(id,"revoked");
                grant=Grant(id);
            }
            string? token=salt==null || grant.Status!="approved"?null:
                Convert.ToHexString(HMACSHA256.HashData(Convert.FromHexString(salt),Convert.FromHexString(claim)));
            if (token!=null) Exec("UPDATE fed_grant SET token_hash=$p1 WHERE id=$p0 AND token_hash IS NULL",id,Hash(token));
            var link=Group(id);
            result=GroupStatus(link,grant,token);
        });
        return result!;
    }

    private GroupSession GroupStatus(GroupLink link,PeerGrant grant,string? token=null)
    {
        var node=FederationNode();
        var roster=LocalGroupRoster(link,grant);
        if (grant.Status is "rejected" or "revoked") roster=roster with { Members=[] };
        return new(link.Id,grant.Status,node.NodeId,node.NodeName!,roster,grant.Status=="approved"?PeerQuota.From(grant):null,grant.AllowUpload,token);
    }

    internal void DecideGroupCollaboration(string id,GroupSelection selection) => Atomic(()=>{
        if (selection.Policy==null) throw new WorkbenchError("请选择常驻协作权限");
        var link=Group(id);
        var grant=Grant(id);
        string action=selection.Policy.Action;
        if (action is "revoke" or "reject")
        {
            if (!(action=="revoke" && grant.Status=="approved" || action=="reject" && grant.Status=="pending"))
                throw new FederationException(409,"invalid_transition");
            StopGroupLink(id,action=="reject"?"rejected":"revoked");
            return;
        }
        var leader=ExternalLeader(link.Topic);
        var members=SelectGroupMembers(leader,selection.Members,selection.MemberMode);
        if (!(action=="approve" && grant.Status=="pending" || action=="configure" && grant.Status=="approved"))
            throw new FederationException(409,"invalid_transition");
        string permission = action=="approve" && link.Endpoint==null
            ? (Share(link.InvitedWindow)?.Permission ?? throw new FederationException(409,"sharing_disabled"))
            : "read_write";
        CheckPeerPolicy(selection.Policy,permission);
        if (selection.Policy.Paused || (grant.PermissionMode=="read_write" && selection.Policy.PermissionMode=="read_only"))
            WithdrawPeerInbox(id);
        Exec("UPDATE fed_group_link SET members=$p1,member_mode=$p2,expires_at=$p3,review_required=0 WHERE id=$p0",id,GroupEncode(members),selection.MemberMode??"legacy",selection.ExpiresAt);
        Exec("UPDATE fed_grant SET window_id=$p1 WHERE id=$p0",id,leader.Id);
        SavePeerPolicy(id,selection.Policy);
        RecheckGroupInbox(Group(id),Grant(id));
    });

    private void StopGroupLink(string id,string status)
    {
        WithdrawPeerInbox(id);
        Exec("UPDATE fed_grant SET status=$p1 WHERE id=$p0",id,status);
        Exec("UPDATE fed_group_mail SET delivery='blocked' WHERE link=$p0 AND incoming=0 AND delivery='pending'",id);
        Exec("DELETE FROM fed_send_file WHERE dispatch IN (SELECT id FROM fed_group_mail WHERE link=$p0 AND incoming=0)",id);
    }

    private GroupLink AuthorizeGroup(string id,string token,string node)
    {
        if (!Read("""
            SELECT 1 FROM fed_grant g JOIN fed_group_link l ON l.id=g.id
            WHERE g.id=$p0 AND g.token_hash=$p1 AND g.node=$p2 AND l.endpoint IS NULL
            """,r=>true,WorkbenchInput.Id(id),Hash(token),node).Any())
            throw new FederationException(403,"invalid_group_grant");
        return Group(id);
    }

    private bool SetRemoteGroup(GroupLink link,GroupRoster roster,string user,string status,PeerQuota? quota,bool upload,string? token)
    {
        CheckRemoteRoster(roster);
        if (link.Remote.Topic.Length>0 && link.Remote.Topic!=roster.Topic)
            throw new FederationException(409,"group_link_conflict");
        if (status=="approved" && link.RemoteStatus!="approved")
            CheckGroupDuplicate(link.Topic,link.Node,roster.Topic,link.InvitedWindow,link.Id);
        var packed=GroupEncode(roster);
        bool change=Exec("""
            UPDATE fed_group_link SET peer_user=$p1,roster=$p2,peer_status=$p3,peer_quota=$p4,peer_upload=$p5,
              token=coalesce($p6,token),share=NULL,seen=unixepoch()
            WHERE id=$p0 AND (peer_user!=$p1 OR roster!=$p2 OR peer_status!=$p3 OR peer_quota IS NOT $p4
               OR peer_upload!=$p5 OR ($p6 IS NOT NULL AND token IS NOT $p6) OR seen<=unixepoch()-12)
            """,link.Id,user,packed,status,quota==null?null:GroupEncode(quota),upload,token)>0;
        if (!change) Exec("UPDATE fed_group_link SET seen=unixepoch() WHERE id=$p0",link.Id);
        if (status is "revoked" or "rejected") StopGroupLink(link.Id,status);
        else if (change) RecheckGroupInbox(Group(link.Id),Grant(link.Id));
        return change;
    }

    internal List<GroupView> GroupCollaborations() =>
        Read($"SELECT {GroupFields} FROM fed_group_link ORDER BY rowid DESC",ReadGroup).Select(link=>{
            var grant=Grant(link.Id);
            string status=grant.Status=="approved" ? link.RemoteStatus : grant.Status;
            if (status=="approved" && (!LocalGroupRoster(link,grant).Active || !link.Remote.Active)) status="paused";
            var contacts=status=="approved"? GroupMembers(link).ToArray():[];
            bool needsInvitation=!link.Connected && status is ("approved" or "pending" or "applying" or "paused")
                && link.Endpoint?.StartsWith("rtc://", StringComparison.Ordinal)==true && Read("""
                    SELECT 1 FROM settings WHERE key='rtc_room.'||$p0 AND julianday(json_extract(value,'$.expiresAt'))<=julianday('now')
                    AND (json_extract(value,'$.resumeTicket') IS NULL OR coalesce(json_array_length(value,'$.bindings'),0)=0)
                    """,_=>true,link.Endpoint[6..]).Any();
            return new GroupView(link.Id,link.Topic,status,link.Endpoint!=null,link.Connected,link.User,link.Remote,LocalGroupRoster(link,grant).Members.Select(m=>m.Id).ToArray(),
                grant,link.RemoteQuota,link.RemoteUpload,grant.Purpose,contacts,link.MemberMode,link.SuggestedMember,link.ExpiresAt,needsInvitation);
        }).ToList();

    private static IEnumerable<GroupRouteMember> GroupMembers(GroupLink link) =>
        link.Remote.Members.Select(member=>GroupMemberTarget(link,member.Id)).Append(GroupMemberTarget(link,null));

    internal IEnumerable<GroupRouteMember> GroupRecipients(string topic,string? caller=null)
    {
        foreach(var link in Read($"SELECT {GroupFields} FROM fed_group_link WHERE topic=$p0 AND peer_status='approved'",ReadGroup,topic))
        {
            var local=LocalGroupRoster(link,Grant(link.Id));
            if (!local.Active || !link.Remote.Active || caller!=null && !local.Members.Any(member=>member.Id==caller)) continue;
            foreach(var member in GroupMembers(link)) yield return member;
        }
    }
}

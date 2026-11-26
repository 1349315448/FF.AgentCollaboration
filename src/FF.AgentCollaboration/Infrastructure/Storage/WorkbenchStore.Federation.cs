using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using FF.AgentCollaboration.Domain;

namespace FF.AgentCollaboration.Infrastructure.Storage;

internal sealed partial class WorkbenchStore
{
    // 节点、分享口令、审批、文件和投递记录分别保存；只有成功扣额的消息能进入终端队列。
    private void InitializeFederation()
    {
        Exec("""
            INSERT OR IGNORE INTO settings VALUES('node_id',lower(hex(randomblob(16))));
            CREATE TABLE IF NOT EXISTS fed_share(window_id TEXT PRIMARY KEY, token TEXT NOT NULL UNIQUE, permission TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS fed_grant(
                id TEXT PRIMARY KEY, window_id TEXT NOT NULL, node TEXT NOT NULL, name TEXT NOT NULL, ip TEXT NOT NULL,
                purpose TEXT NOT NULL, status TEXT NOT NULL, claim_hash TEXT NOT NULL UNIQUE, salt TEXT, token_hash TEXT UNIQUE,
                permission TEXT NOT NULL DEFAULT 'read_only', quota TEXT NOT NULL DEFAULT 'daily', quota_limit INTEGER DEFAULT 20,
                paused INTEGER NOT NULL DEFAULT 0, upload INTEGER NOT NULL DEFAULT 1,
                total INTEGER NOT NULL DEFAULT 0, hour INTEGER NOT NULL DEFAULT 0, hourly INTEGER NOT NULL DEFAULT 0,
                day INTEGER NOT NULL DEFAULT 0, daily INTEGER NOT NULL DEFAULT 0, last_received INTEGER, blocked INTEGER NOT NULL DEFAULT 0,
                streams INTEGER NOT NULL DEFAULT 0);
            CREATE UNIQUE INDEX IF NOT EXISTS fed_open_peer ON fed_grant(window_id,node) WHERE status IN ('pending','approved');
            CREATE TABLE IF NOT EXISTS fed_request(grant_id TEXT NOT NULL, id TEXT NOT NULL, body TEXT NOT NULL, permission TEXT NOT NULL,
                PRIMARY KEY(grant_id,id));
            CREATE TABLE IF NOT EXISTS fed_event(seq INTEGER PRIMARY KEY, grant_id TEXT NOT NULL, id TEXT NOT NULL,
                kind TEXT NOT NULL, request_id TEXT NOT NULL, body TEXT NOT NULL, UNIQUE(grant_id,kind,id));
            CREATE INDEX IF NOT EXISTS fed_stream ON fed_event(grant_id,seq);
            CREATE TABLE IF NOT EXISTS fed_remote(id TEXT PRIMARY KEY, window_id TEXT NOT NULL, endpoint TEXT NOT NULL,
                pin TEXT NOT NULL, provider TEXT NOT NULL, share TEXT, claim TEXT NOT NULL, token TEXT, purpose TEXT NOT NULL,
                name TEXT NOT NULL, agent TEXT NOT NULL, status TEXT NOT NULL, online INTEGER NOT NULL DEFAULT 0,
                connected INTEGER NOT NULL DEFAULT 0, cursor INTEGER NOT NULL DEFAULT 0, last_reply TEXT, error TEXT);
            CREATE TABLE IF NOT EXISTS fed_remote_topic(remote TEXT NOT NULL,topic TEXT NOT NULL,PRIMARY KEY(remote,topic));
            CREATE TABLE IF NOT EXISTS fed_received(remote TEXT NOT NULL, seq INTEGER NOT NULL, kind TEXT NOT NULL,
                request_id TEXT NOT NULL, body TEXT NOT NULL, PRIMARY KEY(remote,seq));
            CREATE TABLE IF NOT EXISTS fed_file(id TEXT PRIMARY KEY,owner TEXT NOT NULL,node TEXT NOT NULL,retry TEXT NOT NULL,
                name TEXT NOT NULL,relative TEXT NOT NULL,size INTEGER NOT NULL,digest TEXT NOT NULL,created INTEGER NOT NULL,
                bound TEXT,UNIQUE(owner,retry));
            CREATE INDEX IF NOT EXISTS fed_file_usage ON fed_file(node,created);
            CREATE TABLE IF NOT EXISTS fed_upload_rate(node TEXT NOT NULL,created INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS fed_upload_window ON fed_upload_rate(node,created);
            CREATE TABLE IF NOT EXISTS fed_incoming(local TEXT PRIMARY KEY,grant_id TEXT NOT NULL,request TEXT NOT NULL,
                upload TEXT, baseline TEXT, audit_done INTEGER NOT NULL DEFAULT 0,active INTEGER NOT NULL DEFAULT 0, UNIQUE(grant_id,request));
            CREATE TABLE IF NOT EXISTS fed_outbox(id TEXT PRIMARY KEY,remote TEXT NOT NULL,window_id TEXT NOT NULL,body TEXT NOT NULL,
                name TEXT,content BLOB,state TEXT NOT NULL DEFAULT 'pending',next_try INTEGER NOT NULL DEFAULT 0,
                started INTEGER NOT NULL DEFAULT 0,created INTEGER NOT NULL DEFAULT (unixepoch()));
            CREATE TABLE IF NOT EXISTS fed_reply_doc(grant_id TEXT NOT NULL,event_id TEXT NOT NULL,file_id TEXT NOT NULL,PRIMARY KEY(grant_id,event_id));
            CREATE TABLE IF NOT EXISTS fed_mail(seq INTEGER PRIMARY KEY,topic TEXT NOT NULL,kind TEXT NOT NULL,source TEXT NOT NULL,
                target TEXT NOT NULL,label TEXT NOT NULL,body TEXT NOT NULL,created TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
            CREATE INDEX IF NOT EXISTS fed_mail_topic ON fed_mail(topic,seq);
            CREATE TABLE IF NOT EXISTS fed_mail_file(seq INTEGER PRIMARY KEY,file_id TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS fed_manual(remote TEXT NOT NULL,id TEXT NOT NULL,topic TEXT NOT NULL,body TEXT NOT NULL,sent INTEGER DEFAULT 0,PRIMARY KEY(remote,id));
            CREATE TABLE IF NOT EXISTS fed_download(remote TEXT NOT NULL,seq INTEGER NOT NULL,doc TEXT NOT NULL,name TEXT NOT NULL,
                next_try INTEGER NOT NULL DEFAULT 0,PRIMARY KEY(remote,seq));
            CREATE INDEX IF NOT EXISTS fed_active_request ON fed_incoming(active) WHERE active>0;
            CREATE INDEX IF NOT EXISTS fed_open_audit ON fed_incoming(audit_done) WHERE baseline IS NOT NULL AND audit_done<2;
            CREATE TRIGGER IF NOT EXISTS fed_release_reader AFTER UPDATE OF online ON windows WHEN NEW.online=0
            BEGIN
                UPDATE fed_incoming SET active=0 WHERE active>0 AND local IN (SELECT id FROM inbox WHERE target=NEW.id);
            END;
            UPDATE fed_incoming SET active=0 WHERE active>0;
            UPDATE fed_outbox SET state='pending',started=2 WHERE state='sending';
            UPDATE fed_remote SET online=0,connected=0;
            UPDATE fed_grant SET streams=0 WHERE streams>0;
            """);
        if (!Read("PRAGMA table_info(fed_remote)", r => r.GetString(1)).Contains("quota_snapshot"))
            Exec("ALTER TABLE fed_remote ADD COLUMN quota_snapshot TEXT");
        if (!Read("PRAGMA table_info(inbox)", r => r.GetString(1)).Contains("work_state"))
            Exec("""
                ALTER TABLE inbox ADD COLUMN work_state TEXT;
                ALTER TABLE inbox ADD COLUMN reply_to TEXT;
                ALTER TABLE inbox ADD COLUMN final_reply INTEGER;
                ALTER TABLE fed_event ADD COLUMN final_reply INTEGER;
                ALTER TABLE fed_received ADD COLUMN final_reply INTEGER;
                ALTER TABLE fed_mail ADD COLUMN task_id TEXT;
                ALTER TABLE fed_outbox ADD COLUMN expects_reply INTEGER NOT NULL DEFAULT 1;
                ALTER TABLE fed_manual ADD COLUMN expects_reply INTEGER NOT NULL DEFAULT 1;
                ALTER TABLE fed_request ADD COLUMN expects_reply INTEGER NOT NULL DEFAULT 1;
                ALTER TABLE fed_incoming ADD COLUMN reader_session TEXT;
                CREATE TRIGGER fed_resume_work AFTER UPDATE OF online ON windows WHEN NEW.online=1 AND OLD.online=0
                AND (SELECT value FROM settings WHERE key='peer_reader.'||NEW.id)='1'
                BEGIN
                    UPDATE fed_incoming SET active=2 WHERE local=(
                        SELECT f.local FROM fed_incoming f JOIN inbox i ON i.id=f.local JOIN fed_grant g ON g.id=f.grant_id
                        WHERE i.target=NEW.id AND i.work_state='working' AND i.acknowledged=0
                          AND f.reader_session=NEW.engine||':'||NEW.session AND g.status='approved' AND g.paused=0
                        ORDER BY i.rowid LIMIT 1);
                END;
                CREATE INDEX fed_received_work ON fed_received(remote,request_id,seq) WHERE kind='receipt';
                CREATE TRIGGER fed_delivery_receipt AFTER UPDATE OF state,acknowledged ON inbox
                WHEN NEW.source='peer-mailbox' AND NEW.work_state IS NOT NULL AND (OLD.state<>NEW.state OR OLD.acknowledged<>NEW.acknowledged)
                BEGIN
                    INSERT INTO fed_event(grant_id,id,kind,request_id,body)
                    SELECT f.grant_id,lower(hex(randomblob(16))),'receipt',f.request,
                           'delivery:'||CASE WHEN NEW.acknowledged THEN 'ignored' ELSE NEW.state END
                    FROM fed_incoming f JOIN fed_grant g ON g.id=f.grant_id
                    WHERE f.local=NEW.id AND g.status='approved' AND g.paused=0;
                END;
                """);
    }

    internal string? FedSetting(string key) => Read("SELECT value FROM settings WHERE key=$p0", r => r.GetString(0), key).SingleOrDefault();
    internal void SetFedSetting(string key, string value) =>
        Exec("INSERT INTO settings VALUES($p0,$p1) ON CONFLICT(key) DO UPDATE SET value=excluded.value", key, value);
    internal FederationNode FederationNode() => new(FedSetting("node_id")!, FedSetting("node_name"), FedSetting("node_ip"), FedSetting("node_public_endpoint"));
    internal void SetFederationNode(string name, string? ip, string? publicEndpoint = null) => Atomic(() =>
    {
        SetFedSetting("node_name", FederationPolicy.Name(name));
        if (ip != null) SetFedSetting("node_ip", ip);
        if (publicEndpoint != null) SetFedSetting("node_public_endpoint", publicEndpoint);
    });

    internal (string Token, string Permission)? Share(string id) =>
        Read("SELECT token,permission FROM fed_share WHERE window_id=$p0", r => ((string, string)?)(r.GetString(0), r.GetString(1)), id).SingleOrDefault();
    internal void SetShare(string id, ShareSettings input) => Atomic(() =>
    {
        var window = Window(id);
        input.Validate();
        bool current = DedicatedReader(id), restricted = input.RestrictedTerminal(current);
        if (restricted != current)
        {
            if (window.Online) throw new WorkbenchError("终端状态已变化，请重新确认重启", WorkbenchFailure.Conflict);
            SetFedSetting("peer_reader." + id, restricted ? "1" : "0");
        }
        if (!input.Enabled)
        {
            foreach (var grant in Read("SELECT id FROM fed_grant WHERE window_id=$p0", r => r.GetString(0), id)) WithdrawPeerInbox(grant);
            Exec("DELETE FROM fed_share WHERE window_id=$p0", id);
            Exec("UPDATE fed_grant SET status='revoked',token_hash=NULL,salt=NULL WHERE window_id=$p0 AND status IN ('approved','pending')", id);
            return;
        }
        Exec("""
            INSERT INTO fed_share VALUES($p0,$p1,$p2) ON CONFLICT(window_id) DO UPDATE SET
            token=CASE WHEN $p3 THEN excluded.token ELSE token END,permission=excluded.permission
            """, id, NewGrant(), input.PermissionMode, input.Rotate);
        if (input.PermissionMode == "read_only")
        {
            foreach (var grant in Read("SELECT id FROM fed_grant WHERE window_id=$p0 AND permission='read_write'", r => r.GetString(0), id)) WithdrawPeerInbox(grant);
            Exec("UPDATE fed_grant SET permission='read_only' WHERE window_id=$p0", id);
        }
    });

    private const string GrantFields = """
        id,window_id,node,name,ip,purpose,status,permission,quota,quota_limit,paused,upload,total,hour,hourly,day,daily,last_received,blocked,streams
        """;
    private static PeerGrant GrantView(SqliteDataReader r)
    {
        string mode = r.GetString(8);
        var period = FederationPolicy.Period(mode, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        long used = mode switch
        {
            "hourly" => r.GetInt64(13) == period.Start ? r.GetInt64(14) : 0,
            "daily" => r.GetInt64(15) == period.Start ? r.GetInt64(16) : 0,
            _ => r.GetInt64(12)
        };
        return new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
            r.GetString(6), r.GetString(7), mode, r.IsDBNull(9) ? null : r.GetInt32(9), r.GetBoolean(10), r.GetBoolean(11),
            used, period.End, r.IsDBNull(17) ? null : r.GetInt64(17), r.GetInt64(18), r.GetInt64(19) > 0 && r.GetString(6) == "approved");
    }

    private PeerGrant Grant(string id) => Read($"SELECT {GrantFields} FROM fed_grant WHERE id=$p0", GrantView, id).SingleOrDefault()
        ?? throw new FederationException(403, "invalid_grant");

    internal FederationSummary FederationSummary() => new(
        Read($"SELECT {GrantFields} FROM fed_grant WHERE window_id IN (SELECT id FROM windows WHERE closed=0) ORDER BY rowid DESC", GrantView),
        RemoteCapsules(), Read("SELECT window_id FROM fed_share WHERE window_id IN (SELECT id FROM windows WHERE closed=0)", r => r.GetString(0)), RecentPeerMail());

    internal void DecidePeer(string id, PeerDecision decision) => Atomic(() =>
    {
        var grant = Grant(id);
        var share = Share(grant.WindowId) ?? throw new FederationException(409, "sharing_disabled");
        Window(grant.WindowId);
        if (decision.Action is "reject" or "revoke")
        {
            string before = decision.Action == "reject" ? "pending" : "approved";
            if (grant.Status != before) throw new FederationException(409, "invalid_transition");
            WithdrawPeerInbox(id);
            Exec("UPDATE fed_grant SET status=$p1,salt=NULL,token_hash=NULL WHERE id=$p0", id, before == "pending" ? "rejected" : "revoked");
            return;
        }
        bool approve = decision.Action == "approve";
        if (!(approve && grant.Status == "pending" || decision.Action == "configure" && grant.Status == "approved"))
            throw new FederationException(409, "invalid_transition");
        FederationPolicy.Permission(decision.PermissionMode, decision.ConfirmWrite);
        if (decision.Paused || grant.PermissionMode == "read_write" && decision.PermissionMode == "read_only") WithdrawPeerInbox(id);
        if (decision.PermissionMode == "read_write" && share.Permission != "read_write") throw new FederationException(403, "window_read_only");
        if (decision.QuotaMode is not ("daily" or "hourly" or "total" or "unlimited") ||
            decision.QuotaMode != "unlimited" && decision.QuotaLimit is not > 0) throw new WorkbenchError("请选择有效的次数配额");
        Exec("""
            UPDATE fed_grant SET status='approved',salt=coalesce(salt,$p1),permission=$p2,quota=$p3,quota_limit=$p4,
            paused=$p5,upload=$p6,total=CASE WHEN $p7 THEN 0 ELSE total END WHERE id=$p0
            """, id, NewGrant(), decision.PermissionMode, decision.QuotaMode, decision.QuotaMode == "unlimited" ? null : decision.QuotaLimit,
            decision.Paused, decision.AllowUpload, decision.ResetTotal && decision.QuotaMode == "total");
    });

    internal PeerSession ApplyPeer(string shareToken, string node, string name, string ip, PeerApplication input)
    {
        if (!FederationPolicy.IsSecret(input.Claim)) throw new FederationException(400, "invalid_claim");
        string purpose = WorkbenchInput.Text(input.Purpose, "申请用途", 300);
        string id = "";
        Atomic(() =>
        {
            string window = Read("""
                SELECT s.window_id FROM fed_share s JOIN windows w ON w.id=s.window_id AND w.closed=0 WHERE s.token=$p0
                """, r => r.GetString(0), shareToken).SingleOrDefault() ?? throw new FederationException(403, "invalid_share");
            if (node == FedSetting("node_id")) throw new FederationException(409, "self_pair");
            string claim = Hash(input.Claim);
            id = Read("SELECT id FROM fed_grant WHERE window_id=$p0 AND node=$p1 AND claim_hash=$p2", r => r.GetString(0), window, node, claim).SingleOrDefault() ?? "";
            if (id.Length != 0) return;
            id = Guid.NewGuid().ToString("N");
            if (Exec("INSERT OR IGNORE INTO fed_grant(id,window_id,node,name,ip,purpose,status,claim_hash) VALUES($p0,$p1,$p2,$p3,$p4,$p5,'pending',$p6)",
                id, window, node, name, ip, purpose, claim) != 1) throw new FederationException(409, "pair_exists");
            Mail(Window(window).TopicId,"application",id,window,name+"#"+node[..8],purpose);
        });
        // POST 响应丢失后重试时，审批可能已经完成；用原有申请凭据恢复同一个授权。
        return ClaimPeer(input.Claim, node);
    }

    private PeerSession Session(string id, string? token = null)
    {
        var grant = Grant(id);
        var node = FederationNode();
        if (grant.Status is "rejected" or "revoked")
            return new(id, grant.Status, node.NodeId, "", "", false, grant.WindowId);
        var window = Window(grant.WindowId);
        return new(id, grant.Status, node.NodeId, node.NodeName!, window.Name, grant.Status == "approved" && window.Online, grant.WindowId, token,
            grant.Status == "approved" ? PeerQuota.From(grant) : null);
    }

    // 申请凭据只用于本人轮询；审批后用独立随机盐派生可重取的 PeerToken，普通授权入口只认其哈希。
    internal PeerSession ClaimPeer(string claim, string node)
    {
        PeerSession session = null!;
        Atomic(() =>
        {
            var match = Read("SELECT id,salt FROM fed_grant WHERE claim_hash=$p0 AND node=$p1", r => (r.GetString(0), Optional(r, 1)), Hash(claim), node);
            if (match.Count != 1) throw new FederationException(403, "invalid_claim");
            var (id, salt) = match[0];
            string? token = null;
            if (salt != null)
            {
                token = Convert.ToHexString(HMACSHA256.HashData(Convert.FromHexString(salt), Convert.FromHexString(claim)));
                Exec("UPDATE fed_grant SET token_hash=$p1 WHERE id=$p0 AND token_hash IS NULL", id, Hash(token));
            }
            session = Session(id, token);
        });
        return session;
    }

    internal string AuthorizePeer(string token, string node) => Read("""
        SELECT g.id FROM fed_grant g JOIN fed_share s ON s.window_id=g.window_id
        JOIN windows w ON w.id=g.window_id AND w.closed=0
        WHERE g.token_hash=$p0 AND g.node=$p1 AND g.status='approved'
        """, r => r.GetString(0), Hash(token), node).SingleOrDefault() ?? throw new FederationException(403, "invalid_grant");

    internal PeerSession PeerStatus(string token, string node) => Session(AuthorizePeer(token, node));
    internal bool OpenPeerStream(string id) =>
        Exec("UPDATE fed_grant SET streams=streams+1 WHERE id=$p0 AND status='approved' AND streams<2", id) == 1;
    internal void ClosePeerStream(string id) => Exec("UPDATE fed_grant SET streams=streams-1 WHERE id=$p0 AND streams>0", id);

    internal object AcceptPeerMessage(string token, string node, string name, PeerMessage input, Action changed)
    {
        string id = WorkbenchInput.Id(input.Id), body = WorkbenchInput.Text(input.Text, "消息", 8000);
        input = input with { Id = id, UploadId = input.UploadId == null ? null : WorkbenchInput.Id(input.UploadId) };
        FederationException? denied = null;
        string permission = "";
        bool inserted = false;
        Atomic(() =>
        {
            var grant = Grant(AuthorizePeer(token, node));
            permission = grant.PermissionMode;
            if (grant.Paused) denied = new(403, "peer_paused");
            if (denied == null)
            {
                var prior = Read("SELECT body,permission,expects_reply FROM fed_request WHERE grant_id=$p0 AND id=$p1", r => (r.GetString(0), r.GetString(1),r.GetBoolean(2)), grant.Id, id);
                if (prior.Count != 0)
                {
                    var upload = Read("SELECT upload FROM fed_incoming WHERE grant_id=$p0 AND request=$p1", r => Optional(r, 0), grant.Id, id).SingleOrDefault();
                    if (prior[0].Item1 != body || upload != input.UploadId || prior[0].Item3!=input.ExpectsReply) throw new FederationException(409, "message_id_conflict");
                    permission = prior[0].Item2;
                    return;
                }
                if (grant.QuotaLimit is int limit && grant.Used >= limit)
                    denied = new(429, "quota_exceeded", new { error = "quota_exceeded", quotaMode = grant.QuotaMode, used = grant.Used, limit, resetAt = grant.ResetAt });
            }
            if (denied != null)
            {
                Exec("UPDATE fed_grant SET blocked=blocked+1 WHERE id=$p0", grant.Id);
                Mail(Window(grant.WindowId).TopicId, "blocked", "peer-mailbox", grant.WindowId, grant.NodeName, denied.Message);
                return;
            }
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Exec("""
                UPDATE fed_grant SET total=total+1,hourly=CASE WHEN hour=$p1 THEN hourly+1 ELSE 1 END,hour=$p1,
                daily=CASE WHEN day=$p2 THEN daily+1 ELSE 1 END,day=$p2,last_received=$p3,name=$p4 WHERE id=$p0
                """, grant.Id, FederationPolicy.Period("hourly", now).Start, FederationPolicy.Period("daily", now).Start, now, name);
            Exec("INSERT INTO fed_request(grant_id,id,body,permission,expects_reply) VALUES($p0,$p1,$p2,$p3,$p4)", grant.Id, id, body, permission,input.ExpectsReply);
            if (input.UploadId != null && !grant.AllowUpload) throw new FederationException(403, "upload_disabled");
            QueuePeerRequest(grant, input, body);
            Exec("INSERT INTO fed_event(grant_id,id,kind,request_id,body) VALUES($p0,$p1,'receipt',$p1,'accepted')", grant.Id, id);
            Exec("INSERT INTO fed_event(grant_id,id,kind,request_id,body) VALUES($p0,lower(hex(randomblob(16))),'receipt',$p1,'work:queued')", grant.Id, id);
            inserted = true;
        });
        if (denied != null || inserted) changed();
        if (denied != null) throw denied;
        return new { id, status = "accepted", permissionMode = permission };
    }

    internal void PeerReply(string grant, PeerMessage input) => Atomic(() => RecordPeerReply(grant, input));

    private void RecordPeerReply(string grant, PeerMessage input)
    {
        string id = WorkbenchInput.Id(input.Id), source = WorkbenchInput.Id(input.RequestId);
        string body = WorkbenchInput.Text(input.Text, "回信", 8000);
        var g = Grant(grant);
        if (g.Status != "approved" || g.Paused) throw new FederationException(403, "invalid_grant");
        var window = Window(g.WindowId);
        if (!Read("SELECT 1 FROM fed_request WHERE grant_id=$p0 AND id=$p1", r => true, grant, source).Any())
            throw new FederationException(404, "unknown_request");
        if (Exec("INSERT OR IGNORE INTO fed_event(grant_id,id,kind,request_id,body,final_reply) VALUES($p0,$p1,'reply',$p2,$p3,$p4)", grant, id, source, body, input.FinalReply) == 0)
        {
            if (!Read("SELECT 1 FROM fed_event WHERE grant_id=$p0 AND id=$p1 AND kind='reply' AND request_id=$p2 AND body=$p3 AND final_reply IS $p4", r => true, grant, id, source, body, input.FinalReply).Any() ||
                Read("SELECT file_id FROM fed_reply_doc WHERE grant_id=$p0 AND event_id=$p1", r => r.GetString(0), grant, id).SingleOrDefault() != input.UploadId)
                throw new FederationException(409, "reply_id_conflict");
            return;
        }
        if (input.UploadId != null)
        {
            if (!g.AllowUpload) throw new FederationException(403, "upload_disabled");
            BindPeerFile(grant, input.UploadId, id);
            Exec("INSERT INTO fed_reply_doc VALUES($p0,$p1,$p2)", grant, id, input.UploadId);
        }
        Mail(window.TopicId, "reply-out", window.Id, "peer-mailbox", g.NodeName, body);
        if (input.FinalReply is bool final)
        {
            var task=Read("""
                SELECT i.id,i.source,i.work_state,i.return_hint IS NOT NULL FROM inbox i JOIN fed_incoming f ON f.local=i.id
                WHERE f.grant_id=$p0 AND f.request=$p1 AND i.acknowledged=0
                """,r=>new ReceivedTask(r.GetString(0),r.GetString(1),Optional(r,2),r.GetBoolean(3)),grant,source).SingleOrDefault();
            if (task!=null) RecordTaskReply(task,final);
        }
        if (input.FinalReply!=false)
            Exec("UPDATE fed_incoming SET active=0 WHERE grant_id=$p0 AND request=$p1", grant, source);
    }

    internal object RecentPeerRequests(string grant)
    {
        Window(Grant(grant).WindowId);
        return Read("""
            SELECT id,body,permission FROM fed_request WHERE grant_id=$p0 ORDER BY rowid DESC LIMIT 20
            """, r => new { id = r.GetString(0), text = r.GetString(1), permissionMode = r.GetString(2) }, grant);
    }

    internal List<PeerEvent> PeerEvents(string grant, long after) => Read("""
        SELECT e.seq,e.kind,e.request_id,e.body,d.file_id,f.name,e.final_reply FROM fed_event e
        LEFT JOIN fed_reply_doc d ON e.kind='reply' AND d.grant_id=e.grant_id AND d.event_id=e.id LEFT JOIN fed_file f ON f.id=d.file_id
        WHERE e.grant_id=$p0 AND e.seq>$p1 ORDER BY e.seq LIMIT 32
        """, r => new PeerEvent(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), Optional(r, 4), Optional(r, 5), r.IsDBNull(6) ? null : r.GetBoolean(6)), grant, after);
}

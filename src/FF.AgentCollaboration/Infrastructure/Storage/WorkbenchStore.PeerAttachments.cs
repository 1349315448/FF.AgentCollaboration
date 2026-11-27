using System.Text.Json;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Files;

namespace FF.AgentCollaboration.Infrastructure.Storage;

internal sealed partial class WorkbenchStore
{
    internal object QueuePeerAttachments(string grant, string? address, string? text, string? original, string[] paths)
    {
        var caller = Caller(grant);
        if (DedicatedReader(caller.Id)) throw new WorkbenchError("专用审阅 Agent 不能发送项目附件", WorkbenchFailure.Forbidden);
        if (paths.Length is < 1 or > 20) throw new WorkbenchError("请选择 1 至 20 个项目附件");
        if ((address != null) == (original != null)) throw new WorkbenchError("请选择收件 Agent 或提供要补附件的原消息 ID");
        string? messageId = original == null ? null : WorkbenchInput.Id(original);
        string target = messageId != null
            ? Read("SELECT remote FROM fed_outbox WHERE id=$p0 AND window_id=$p1 AND state='accepted' AND attach_to IS NULL",
                r => r.GetString(0), messageId, caller.Id).SingleOrDefault()
                ?? throw new WorkbenchError("只能向本 Agent 已送达的原消息补加附件", WorkbenchFailure.Forbidden)
            : RemoteAddress(caller, WorkbenchInput.Text(address, "收件 Agent", 160))?.Id
                ?? throw new WorkbenchError("请选一个已授权的外部 Agent");
        var browser = new ProjectFileBrowser(caller.Project);
        var attachments = new List<OutgoingAttachment>();
        long total = 0;
        foreach (string relative in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string path = browser.Resolve(relative);
            string name = SafePeerName(Path.GetFileName(path));
            using var file = ProjectFileBrowser.OpenVerified(path);
            if (file.Length > PeerAttachmentLimit(name) || (total += file.Length) > 20L * 1024 * 1024)
                throw new WorkbenchError("单次发送的附件总量不得超过 20 MiB，Markdown 仍限 1 MiB");
            var bytes = new byte[(int)file.Length];
            file.ReadExactly(bytes);
            ValidatePeerBytes(name, bytes);
            attachments.Add(new(Guid.NewGuid().ToString("N"), name, bytes));
        }
        string body = messageId == null ? WorkbenchInput.Text(text, "消息", 8000) : "补充附件";
        string id = Guid.NewGuid().ToString("N");
        string status = "";
        Atomic(() =>
        {
            Caller(grant);
            var queued = Read("""
                SELECT count(*),coalesce(sum(length(f.content)),0)
                FROM fed_send_file f JOIN fed_outbox o ON o.id=f.dispatch WHERE o.state IN ('awaiting_user','pending','sending')
                """, r => (r.GetInt64(0), r.GetInt64(1))).Single();
            if (queued.Item1 + attachments.Count > 250 || queued.Item2 + total > 100L * 1024 * 1024)
                throw new WorkbenchError("待发送附件已达到容量上限，请先处理待审请求");
            var recipient = RemoteRecipients(caller.TopicId, caller.Id).FirstOrDefault(r => r.Id == target)
                ?? throw new WorkbenchError("外部授权已失效", WorkbenchFailure.Forbidden);
            status = AttachmentPermission(caller.Id) ? "pending" : "awaiting_user";
            Exec("INSERT INTO fed_outbox(id,remote,window_id,body,state,expects_reply,attach_to) VALUES($p0,$p1,$p2,$p3,$p4,0,$p5)",
                id, target, caller.Id, body, status, messageId);
            foreach (var file in attachments)
                Exec("INSERT INTO fed_send_file VALUES($p0,$p1,$p2,$p3)", file.Id, id, file.Name, file.Content);
            if (messageId == null)
                Mail(caller.TopicId, "outgoing", caller.Id, target, recipient.AgentName + " · " + recipient.NodeName, body, taskId:id);
        });
        return new { id, status = status == "pending" ? "queued" : status };
    }

    internal List<PeerAttachmentApproval> AttachmentApprovals() => Read("""
        SELECT o.id,w.id,w.name,r.agent||' · '||r.name,
            (SELECT json_group_array(name) FROM fed_send_file f WHERE f.dispatch=o.id),o.attach_to
        FROM fed_outbox o JOIN fed_remote r ON r.id=o.remote JOIN windows w ON w.id=o.window_id AND w.closed=0
        WHERE o.state='awaiting_user' ORDER BY o.rowid
        """, r => new PeerAttachmentApproval(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
        JsonSerializer.Deserialize<string[]>(r.GetString(4))!, Optional(r,5)));

    internal bool AttachmentPermission(string window) =>
        Read("SELECT 1 FROM fed_attachment_right WHERE window=$p0", _ => true, window).Any();

    internal void RevokeAttachmentPermission(string window) =>
        Exec("DELETE FROM fed_attachment_right WHERE window=$p0", Window(WorkbenchInput.Id(window)).Id);

    internal void DecideAttachment(string request, string action) => Atomic(() =>
    {
        string id = WorkbenchInput.Id(request);
        var item = AttachmentApprovals().FirstOrDefault(entry => entry.Id == id)
            ?? throw new WorkbenchError("附件请求已处理", WorkbenchFailure.Conflict);
        if (action == "reject")
        {
            Exec("UPDATE fed_outbox SET state='rejected' WHERE id=$p0", id);
            Exec("DELETE FROM fed_send_file WHERE dispatch=$p0", id);
            return;
        }
        if (action is not ("once" or "always")) throw new WorkbenchError("请选择授权方式");
        if (action == "always") Exec("INSERT OR IGNORE INTO fed_attachment_right VALUES($p0)", item.AgentId);
        Exec("UPDATE fed_outbox SET state='pending' WHERE id=$p0", id);
    });

    internal List<OutgoingAttachment> OutgoingAttachments(string dispatch) =>
        Read("SELECT id,name,content FROM fed_send_file WHERE dispatch=$p0 ORDER BY rowid",
            r => new OutgoingAttachment(r.GetString(0), r.GetString(1), (byte[])r[2]), dispatch);

    private string[] BindAttachments(PeerGrant grant, string dispatch, string original, string local, string[] ids)
    {
        if (ids.Length == 0) return [];
        if (!grant.AllowUpload) throw new FederationException(403, "upload_disabled");
        return ids.Select(file =>
        {
            string path = BindPeerFile(grant.Id, file, dispatch);
            Exec("INSERT OR IGNORE INTO fed_attachment VALUES($p0,$p1,$p2,$p3)", dispatch, file, original, local);
            return path;
        }).ToArray();
    }

    private object ReceiveSupplement(PeerGrant grant, PeerMessage input, string? sender)
    {
        string original = WorkbenchInput.Id(input.AttachTo);
        if (input.Id == original) throw new FederationException(409, "file_parent_conflict");
        string local = Read("""
            SELECT i.local FROM fed_incoming i JOIN fed_request r ON r.id=i.request AND r.grant_id=i.grant_id
            WHERE i.grant_id=$p0 AND i.request=$p1 AND r.sender IS $p2
            """, r => r.GetString(0), grant.Id, original, sender).SingleOrDefault() ??
            throw new FederationException(403, "file_parent_unavailable");
        var prior = Read("SELECT local,file FROM fed_attachment WHERE dispatch=$p0 AND original=$p1 ORDER BY file",
            r => (r.GetString(0), r.GetString(1)), input.Id, original);
        if (prior.Count != 0)
        {
            if (prior.Any(item => item.Item1 != local) ||
                !prior.Select(item => item.Item2).SequenceEqual(input.AttachmentIds!.Order(StringComparer.Ordinal)))
                throw new FederationException(409, "attachment_id_conflict");
            return new { input.Id, status = "accepted", permissionMode = grant.PermissionMode };
        }
        var paths = BindAttachments(grant, input.Id, original, local, input.AttachmentIds!);
        var existing = Preview(local).Files;
        Exec("UPDATE inbox SET files=$p1 WHERE id=$p0", local,
            JsonSerializer.Serialize(existing.Concat(paths).Distinct(StringComparer.OrdinalIgnoreCase)));
        return new { input.Id, status = "accepted", permissionMode = grant.PermissionMode };
    }

    internal object MailAttachments(long seq) => Read("""
        SELECT f.id,f.name,f.size FROM fed_mail m JOIN fed_attachment a ON a.local=m.task_id JOIN fed_file f ON f.id=a.file
        WHERE m.seq=$p0 AND m.kind='incoming' ORDER BY a.rowid
        """, r => new { id=r.GetString(0), name=r.GetString(1), bytes=r.GetInt64(2) }, seq);

    internal (string Name, byte[] Bytes) MailAttachment(long seq, string id)
    {
        var found = Read("""
            SELECT f.name,f.relative FROM fed_mail m JOIN fed_attachment a ON a.local=m.task_id JOIN fed_file f ON f.id=a.file
            WHERE m.seq=$p0 AND m.kind='incoming' AND f.id=$p1
            """, r => (r.GetString(0), r.GetString(1)), seq, WorkbenchInput.Id(id)).SingleOrDefault();
        if (found.Item1 == null) throw new FederationException(404, "file_expired");
        var path = PeerPath(found.Item2);
        using var stream = ProjectFileBrowser.OpenVerified(path);
        return (found.Item1, PeerAttachmentBytes(stream, found.Item1, CancellationToken.None).GetAwaiter().GetResult());
    }
}

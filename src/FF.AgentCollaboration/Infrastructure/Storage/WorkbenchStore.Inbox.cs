using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Files;

namespace FF.AgentCollaboration.Infrastructure.Storage;

internal sealed partial class WorkbenchStore
{
    private const string InboxColumns = """
        id,target,sender,text,files,document,return_hint,state,document_ref,
        (SELECT t.name FROM windows s JOIN topics t ON t.id=s.topic_id
         WHERE s.id=inbox.source AND s.topic_id<>inbox.topic_id),source,acknowledged
        """;

    private Func<SqliteDataReader, InboxItem> ReadInboxItem()
    {
        string directory = Handoffs;
        return row =>
        {
            string? reference = Optional(row, 8);
            bool managed = row.GetBoolean(5);
            string? path = managed ? DocumentPath(directory, reference ?? row.GetString(0)) : reference;
            return new(row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3),
                JsonSerializer.Deserialize<string[]>(row.GetString(4))!, path, Optional(row, 6), row.GetString(7))
            { ProjectDocument = !managed && reference != null, FromTopic = Optional(row, 9), Source = row.GetString(10), Acknowledged = row.GetBoolean(11) };
        };
    }

    private AgentWindow Recipient(AgentWindow source, string targetAddress)
    {
        string address = targetAddress.Trim();
        bool mention = address.StartsWith('@');
        if (mention) address = address[1..];
        AgentWindow? target = !mention && Guid.TryParseExact(address, "N", out _) ? Window(address) : null;
        if (target == null)
        {
            int slash = address.IndexOf('/');
            string? namedTopic = slash > 0 ? Read("SELECT id FROM topics WHERE name=$p0",
                row => row.GetString(0), address[..slash]).SingleOrDefault() : null;
            // 存在指定话题时只在该话题内查找。旧窗口别名中的斜杠仍可按本话题名称查找。
            target = Read($"SELECT {AgentColumns} FROM windows WHERE closed=0 AND name=$p0 AND topic_id=$p1",
                Agent, namedTopic == null ? address : address[(slash + 1)..], namedTopic ?? source.TopicId)
                .SingleOrDefault() ?? throw new WorkbenchError("找不到收件 Agent", WorkbenchFailure.NotFound);
        }
        if (source.Id == target.Id) throw new WorkbenchError("请选择另一个 Agent");
        return target;
    }

    internal InboxItem Send(string grant, string targetAddress, string message, IReadOnlyList<string> files,
        string? returnHint, string? document, bool supersedePrevious)
    {
        InboxItem? record = null;
        string? createdDocument = null;
        try
        {
            Atomic(() =>
            {
                var source = Caller(grant);
                var target = Recipient(source, targetAddress);
                string? reference = document == null ? null : ProjectMarkdown(source.Project, Topic(source.TopicId).Folder, document);
                string[] attachments = files.Select(file => ProjectFolders.Attachment(source.Project, file)).ToArray();
                string id = Guid.NewGuid().ToString("N");
                string summary = message;
                bool longMessage = reference == null && Encoding.UTF8.GetByteCount(message) > 2048;
                string? managedReference = null;
                if (longMessage)
                {
                    string folder = Topic(target.TopicId).Folder!;
                    bool global = CustomPaths || Read("SELECT value FROM settings WHERE key='standalone_handoffs'", r => r.GetString(0)).SingleOrDefault() == "1";
                    string path = DocumentPath(global ? Handoffs : Path.Combine(folder, "handoffs"), id);
                    managedReference = global ? id : path;
                    TopicArchive.CheckPath(path);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                    {
                        createdDocument = path;
                        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                        writer.Write(message);
                    }
                    summary = string.Join(' ', message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                    summary = summary.Length > 220 ? summary[..220] + "…" : summary;
                }
                if (supersedePrevious) Supersede(source.Id, target.Id);
                Exec("""
                    INSERT INTO inbox(id,source,target,sender,text,files,document,return_hint,state,document_ref,topic_id,created_at)
                    VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,'pending',$p8,$p9,CURRENT_TIMESTAMP)
                    """, id, source.Id, target.Id, source.Name, summary, JsonSerializer.Serialize(attachments), longMessage, returnHint,
                    managedReference ?? reference, target.TopicId);
                record = new(id, target.Id, source.Name, summary, attachments, createdDocument ?? reference, returnHint, "pending")
                { ProjectDocument = reference != null, QueueHint = QueueHint(target) };
            });
            return record!;
        }
        catch
        {
            if (createdDocument != null) File.Delete(createdDocument);
            throw;
        }
    }

    private static string DocumentPath(string directory, string reference) =>
        Path.IsPathFullyQualified(reference) ? reference : Path.Combine(directory, reference + ".md");

    private static string ProjectMarkdown(string project, string? archive, string path)
    {
        string file = Path.GetFullPath(path, project);
        if (archive != null)
        {
            string archived = Path.GetFullPath(path, archive);
            if (Path.IsPathFullyQualified(path) || !File.Exists(file))
            {
                string relative = Path.GetRelativePath(archive, archived);
                if (!Path.IsPathRooted(relative) && relative.Split(Path.DirectorySeparatorChar)[0] != "..")
                {
                    TopicArchive.CheckPath(archive);
                    project = archive;
                    file = archived;
                }
            }
        }
        file = ProjectFolders.Attachment(project, file);
        if (!Path.GetExtension(file).Equals(".md", StringComparison.OrdinalIgnoreCase)
            || Path.GetRelativePath(project, file).Contains(':') || File.GetAttributes(project).HasFlag(FileAttributes.ReparsePoint))
            throw new WorkbenchError("交接文档必须是发件项目或所属话题目录内的 Markdown 文件");
        return file;
    }

    // [T05] 重发只产生新的待投递记录。权限、目标及原始内容在同一事务中确定。
    internal InboxItem Resend(string grant, string originalId, string? target, bool supersedePrevious)
    {
        InboxItem? record = null;
        Atomic(() =>
        {
            var source = Caller(grant);
            var original = Read($"SELECT {InboxColumns} FROM inbox WHERE id=$p0 AND source=$p1",
                ReadInboxItem(), originalId, source.Id).SingleOrDefault() ?? throw new WorkbenchError("只能重发自己发出的消息", WorkbenchFailure.Forbidden);
            var recipient = target == null ? Window(original.Target) : Recipient(source, target);
            if (original.ProjectDocument) ProjectMarkdown(source.Project, Topic(source.TopicId).Folder, original.Document!);
            if (supersedePrevious) Supersede(source.Id, recipient.Id);
            string id = Guid.NewGuid().ToString("N");
            Exec("""
                INSERT INTO inbox(id,source,target,sender,text,files,document,return_hint,state,document_ref,topic_id,created_at)
                SELECT $p1,source,$p2,sender,text,files,document,return_hint,'pending',
                       CASE WHEN document=1 THEN coalesce(document_ref,id) ELSE document_ref END,$p3,CURRENT_TIMESTAMP
                FROM inbox WHERE id=$p0
                """, originalId, id, recipient.Id, recipient.TopicId);
            record = original with { Id = id, Target = recipient.Id, State = "pending", Acknowledged = false, QueueHint = QueueHint(recipient) };
        });
        return record!;
    }

    private const string Revocable = "acknowledged=0 AND state IN ('pending','delivery_uncertain')";

    private void Supersede(string source, string target) =>
        Exec($"UPDATE inbox SET acknowledged=1 WHERE source=$p0 AND target=$p1 AND {Revocable}", source, target);

    internal string Cancel(string grant, string? id, string? target)
    {
        string? cancelled = null;
        Atomic(() =>
        {
            var source = Caller(grant);
            if (target != null)
            {
                var recipient = Recipient(source, target);
                id = Read($"SELECT id FROM inbox WHERE source=$p0 AND target=$p1 AND {Revocable} ORDER BY rowid DESC LIMIT 1",
                    row => row.GetString(0), source.Id, recipient.Id).SingleOrDefault();
            }
            if (id == null || Exec($"UPDATE inbox SET acknowledged=1 WHERE source=$p0 AND id=$p1 AND {Revocable}", source.Id, id) != 1)
                throw new WorkbenchError("没有可撤回的本人待收或待核对消息", WorkbenchFailure.Conflict);
            cancelled = id;
        });
        return cancelled!;
    }

    internal string ReadDocument(string grant, string pathOrId)
    {
        var caller = Caller(grant);
        bool byPath = Path.IsPathFullyQualified(pathOrId);
        string? path = byPath ? Path.GetFullPath(pathOrId) : null;
        bool shortId = !byPath && pathOrId.Length == 8 && pathOrId.All(char.IsAsciiHexDigit);
        string? idPattern = byPath ? null : shortId ? pathOrId.ToLowerInvariant() + "*" : WorkbenchInput.Id(pathOrId);
        string? managedId = null;
        if (path != null)
        {
            string stem = Path.GetFileNameWithoutExtension(path);
            if (Guid.TryParseExact(stem, "N", out _) && Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase) &&
                (ProjectFolders.Same(path, DocumentPath(Handoffs, stem)) || ProjectFolders.Same(path, DocumentPath(LegacyHandoffs, stem)) ||
                 Read("SELECT 1 FROM settings WHERE key LIKE 'handoffs.previous.%' AND value=$p0 COLLATE NOCASE",
                     r => r.GetInt32(0), Path.GetDirectoryName(path)).Count != 0))
                managedId = stem;
        }
        var matches = Read("""
            SELECT i.document,coalesce(i.document_ref,i.id),w.project,t.folder,i.document_ref IS NOT NULL FROM inbox i
            LEFT JOIN windows w ON w.id=i.source LEFT JOIN topics t ON t.id=w.topic_id
            WHERE (i.source=$p0 OR i.target=$p0)
              AND (i.id GLOB $p1 OR i.document_ref=$p2 COLLATE NOCASE
                   OR (i.document=1 AND coalesce(i.document_ref,i.id)=$p3 COLLATE NOCASE))
            LIMIT 2
            """, row => new { Managed = row.GetBoolean(0), Reference = row.GetString(1), Project = Optional(row, 2), Folder = Optional(row, 3), HasReference = row.GetBoolean(4) },
            caller.Id, idPattern, path, managedId);
        if (shortId && matches.Count > 1) throw new WorkbenchError("短编号对应多条收发消息，请使用 HANDOFF_DOC 路径或完整消息编号", WorkbenchFailure.Conflict);
        var entry = matches.FirstOrDefault() ?? throw new WorkbenchError("不能读取该交接文档", WorkbenchFailure.Forbidden);
        if (!entry.Managed && !entry.HasReference) throw new WorkbenchError("这条消息没有交接文档，请直接查看终端中的 MSG 正文");
        // 项目文档可能在发出后被替换，读取时重新检查文件和目录。
        if (!entry.Managed)
            return File.ReadAllText(ProjectMarkdown(entry.Project ?? throw new WorkbenchError("找不到文档来源项目", WorkbenchFailure.NotFound), entry.Folder, entry.Reference));
        string managedPath = DocumentPath(Handoffs, entry.Reference);
        TopicArchive.CheckPath(managedPath);
        return File.ReadAllText(managedPath);
    }

    internal List<InboxItem> Inbox(string target) => Read($"""
        SELECT {InboxColumns} FROM inbox
        WHERE target=$p0 AND state IN ('pending','writing','delivery_uncertain') AND acknowledged=0 ORDER BY rowid
        """, ReadInboxItem(), target);
    // 忽略保留原投递状态和文档，同时阻止尚未认领的消息写入终端。
    internal void Acknowledge(string target, string id)
    {
        if (Exec("UPDATE inbox SET acknowledged=1 WHERE target=$p0 AND id=$p1 AND state IN ('pending','delivery_uncertain')", target, id) == 0)
            throw new WorkbenchError("这条消息当前无法忽略", WorkbenchFailure.Conflict);
    }

    internal int AcknowledgeAll(string target) =>
        Exec("UPDATE inbox SET acknowledged=1 WHERE target=$p0 AND acknowledged=0 AND state IN ('pending','delivery_uncertain')", target);

    internal InboxItem Preview(string id) => Read($"SELECT {InboxColumns} FROM inbox WHERE id=$p0",
        ReadInboxItem(), id).SingleOrDefault() ?? throw new WorkbenchError("消息不存在", WorkbenchFailure.NotFound);

    // 后台话题的消息可能在相邻两次快照之间送达。动画仅需要最近的收发端和状态。
    internal List<HandoffRoute> RecentHandoffs() => Read("""
        SELECT id,source,target,state,acknowledged FROM inbox ORDER BY rowid DESC LIMIT 30
        """, row => new HandoffRoute(row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3), row.GetBoolean(4)));

    // 当前话题的收件及各发件窗口各取指定条数，合并后按插入顺序展示。
    internal List<TimelineEntry> Timeline(string topic, int recent) => Read("""
        WITH recent(seq) AS (
            SELECT seq FROM (SELECT rowid AS seq FROM inbox WHERE topic_id=$p0 ORDER BY rowid DESC LIMIT $p1)
            UNION
            SELECT i.rowid FROM windows w JOIN inbox i ON i.rowid IN
                (SELECT rowid FROM inbox WHERE source=w.id ORDER BY rowid DESC LIMIT $p1) WHERE w.topic_id=$p0
        )
        SELECT i.id,i.source,i.target,CASE WHEN s.topic_id<>$p0 THEN st.name||'/'||i.sender ELSE i.sender END,
               CASE WHEN i.topic_id<>$p0 THEN tt.name||'/'||t.name ELSE t.name END,substr(i.text,1,96),i.state,strftime('%Y-%m-%dT%H:%M:%SZ',i.created_at),i.acknowledged
        FROM recent r JOIN inbox i ON i.rowid=r.seq JOIN windows t ON t.id=i.target JOIN topics tt ON tt.id=i.topic_id
        LEFT JOIN windows s ON s.id=i.source LEFT JOIN topics st ON st.id=s.topic_id ORDER BY r.seq DESC LIMIT $p1
        """, row => new TimelineEntry(row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3),
            row.GetString(4), row.GetString(5), row.GetString(6), Optional(row, 7), row.GetBoolean(8)), topic, recent);

    internal InboxItem Pending(string target, string id) => Read($"""
        SELECT {InboxColumns} FROM inbox WHERE id=$p0 AND target=$p1 AND state='pending' AND acknowledged=0
        """, ReadInboxItem(), id, target).SingleOrDefault() ?? throw new WorkbenchError("这条消息已处理或不存在", WorkbenchFailure.Conflict);
    internal List<InboxItem> Automatic() => Read($"""
        SELECT {InboxColumns} FROM inbox WHERE rowid IN (
            SELECT min(i.rowid) FROM inbox AS i JOIN windows AS w ON w.id=i.target JOIN topics t ON t.id=i.topic_id AND t.status='active'
            WHERE i.state='pending' AND i.acknowledged=0 AND w.closed=0 AND w.online=1 AND w.auto_receive=1 GROUP BY i.target
        ) ORDER BY rowid
        """, ReadInboxItem());
    internal bool ClaimMessage(string id, bool automatic) => Exec("""
        UPDATE inbox SET state='writing' WHERE id=$p0 AND state='pending' AND acknowledged=0 AND EXISTS(
            SELECT 1 FROM windows JOIN topics t ON t.id=windows.topic_id WHERE windows.id=inbox.target AND closed=0 AND online=1
            AND ($p1=0 OR (auto_receive=1 AND t.status='active'))
        )
        """, id, automatic) == 1;
    internal void FinishMessage(string id, string state) =>
        Exec("UPDATE inbox SET state=$p1 WHERE id=$p0 AND state='writing'", id, state);
}

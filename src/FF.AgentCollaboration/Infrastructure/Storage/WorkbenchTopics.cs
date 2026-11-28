using Microsoft.Data.Sqlite;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Files;

namespace FF.AgentCollaboration.Infrastructure.Storage;

internal sealed partial class WorkbenchStore
{
    private const string TopicColumns = "id,name,project,goal,status,folder,created_at,finished_at,finish_summary,leader_window_id";

    // [T07] 复用启动事务；仅为旧数据创建默认话题，空库留给用户创建第一个话题。
    private void InitializeTopics()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS topics(
                id TEXT PRIMARY KEY, name TEXT NOT NULL COLLATE NOCASE UNIQUE, project TEXT NOT NULL, goal TEXT,
                status TEXT NOT NULL DEFAULT 'active' CHECK(status IN ('active','finished')), folder TEXT,
                created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP, finished_at TEXT
            )
            """);
        if (!Read("PRAGMA table_info(topics)", row => row.GetString(1)).Contains("finish_summary"))
            Exec("ALTER TABLE topics ADD COLUMN finish_summary TEXT");
        if (!Read("PRAGMA table_info(topics)", row => row.GetString(1)).Contains("leader_window_id"))
            Exec("ALTER TABLE topics ADD COLUMN leader_window_id TEXT");
        if (!Read("PRAGMA table_info(windows)", row => row.GetString(1)).Contains("topic_id"))
        {
            Exec("ALTER TABLE windows ADD COLUMN topic_id TEXT NOT NULL DEFAULT ''");
            if (Read("SELECT 1 FROM windows LIMIT 1", row => row.GetInt32(0)).Count > 0)
            {
                string id = Guid.NewGuid().ToString("N");
                var legacyProject = ProjectSetting() ?? Read("SELECT project FROM windows ORDER BY rowid LIMIT 1", row => row.GetString(0)).Single();
                Exec("INSERT INTO topics(id,name,project) VALUES($p0,'默认话题',$p1)", id, legacyProject);
                Exec("UPDATE windows SET topic_id=$p0", id);
            }
        }
        if (!Read("PRAGMA table_info(inbox)", row => row.GetString(1)).Contains("topic_id"))
        {
            Exec("ALTER TABLE inbox ADD COLUMN topic_id TEXT NOT NULL DEFAULT ''");
            Exec("UPDATE inbox SET topic_id=w.topic_id FROM windows w WHERE inbox.target=w.id");
        }
        Exec("""
            CREATE UNIQUE INDEX IF NOT EXISTS topic_archive_folder ON topics(folder COLLATE NOCASE) WHERE folder IS NOT NULL;
            DROP INDEX IF EXISTS terminal_open_name;
            CREATE UNIQUE INDEX IF NOT EXISTS terminal_open_topic_name ON windows(topic_id,name) WHERE closed=0
            """);
    }

    private static WorkbenchTopic ReadTopic(SqliteDataReader row) => new(row.GetString(0), row.GetString(1), row.GetString(2),
        Optional(row, 3), row.GetString(4), Optional(row, 5), row.GetString(6), Optional(row, 7), Optional(row, 8), Optional(row, 9));

    internal List<WorkbenchTopic> Topics() => Read($"SELECT {TopicColumns} FROM topics ORDER BY rowid", ReadTopic);
    internal WorkbenchTopic Topic(string id) => Read($"SELECT {TopicColumns} FROM topics WHERE id=$p0", ReadTopic, id)
        .SingleOrDefault() ?? throw new WorkbenchError("找不到这个话题", WorkbenchFailure.NotFound);

    // 仅本机人工入口调用。确认时的快照已过期则需重新查看当前组长。
    internal void SetTopicLeader(string topicId, string? windowId, string? expectedLeaderWindowId = null) => Atomic(() =>
    {
        var topic = Topic(topicId);
        if (topic.Status != "active")
            throw new WorkbenchError("请先重新打开这个话题", WorkbenchFailure.Conflict);
        if (expectedLeaderWindowId != null && expectedLeaderWindowId != (topic.LeaderWindowId ?? ""))
            throw new WorkbenchError("话题组长已变更，请重新确认", WorkbenchFailure.Conflict);
        if (windowId != null && Window(windowId).TopicId != topicId)
            throw new WorkbenchError("组长必须是当前话题的 Agent");
        Exec("UPDATE topics SET leader_window_id=$p1 WHERE id=$p0", topicId, windowId);
    });

    internal WorkbenchTopic SaveTopic(string? id, TopicInput input)
    {
        string name = WorkbenchInput.Text(input.Name, "话题名", 80);
        if (name.Any(char.IsControl) || name.Contains('/') || name.Contains('@'))
            throw new WorkbenchError("话题名不能包含 /、@ 或控制字符");
        string? goal = input.Goal?.Trim();
        try
        {
            if (id == null)
            {
                id = Guid.NewGuid().ToString("N");
                string project = ProjectFolders.Resolve(input.Project ?? ProjectList().Current);
                Atomic(() =>
                {
                    Exec("INSERT INTO topics(id,name,project,goal) VALUES($p0,$p1,$p2,$p3)", id, name, project, goal);
                    BindArchive(Topic(id), input.Folder);
                });
            }
            else Exec("UPDATE topics SET name=$p1,goal=$p2 WHERE id=$p0", id, name, goal);
        }
        catch (SqliteException error) when (error.SqliteExtendedErrorCode == 2067)
        {
            throw new WorkbenchError("这个话题名已经使用", WorkbenchFailure.Conflict);
        }
        return Topic(id);
    }

    private string ArchiveRoot => Read("SELECT value FROM settings WHERE key='archive_root'", row => row.GetString(0)).Single();

    internal List<FolderEntry> ArchiveChoices()
    {
        string parent = ArchiveRoot;
        TopicArchive.CheckPath(parent);
        if (!Directory.Exists(parent)) return [];
        var used = Topics().Where(t => t.Folder != null).Select(t => t.Folder!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new DirectoryInfo(parent).EnumerateDirectories("*", new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint })
            .Where(d => !d.Name.StartsWith('.') && !used.Contains(d.FullName)).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(d => new FolderEntry(d.Name, d.FullName)).ToList();
    }

    private void BindArchive(WorkbenchTopic topic, string? existing)
    {
        if (!string.IsNullOrWhiteSpace(existing))
        {
            existing = ProjectFolders.Resolve(existing);
            if (Read("SELECT 1 FROM topics WHERE folder=$p0 COLLATE NOCASE", row => row.GetInt32(0), existing).Count != 0)
                throw new WorkbenchError("这个目录已关联其他话题", WorkbenchFailure.Conflict);
        }
        string folder = TopicArchive.Create(ArchiveRoot, topic, existing);
        Exec("UPDATE topics SET folder=$p1 WHERE id=$p0", topic.Id, folder);
    }

    internal WorkbenchTopic FinishOwnTopic(string grant, string summary)
    {
        WorkbenchTopic topic = null!;
        Atomic(() => topic = SetFinishedCore(Caller(grant).TopicId, true, summary));
        return topic;
    }

    internal WorkbenchTopic SetFinished(string id, bool finished)
    {
        WorkbenchTopic topic = null!;
        Atomic(() => topic = SetFinishedCore(id, finished, null));
        return topic;
    }

    private WorkbenchTopic SetFinishedCore(string id, bool finished, string? summary)
    {
        Exec("""
            UPDATE topics SET status=$p1,finished_at=CASE WHEN $p1='finished' THEN coalesce(finished_at,CURRENT_TIMESTAMP) END,
                finish_summary=CASE WHEN $p1='finished' THEN coalesce(finish_summary,$p2) END WHERE id=$p0
            """, id, finished ? "finished" : "active", summary);
        var topic = Topic(id);
        if (finished) WriteCollaborationLog(topic);
        return topic;
    }

    private void WriteCollaborationLog(WorkbenchTopic topic)
    {
        string folder = topic.Folder!;
        static string Cell(string value) => value.ReplaceLineEndings(" ").Replace("|", "\\|");
        var lines = new System.Text.StringBuilder().Append("# ").Append(topic.Name).AppendLine(" · 协作记录").AppendLine();
        if (topic.FinishSummary != null) lines.AppendLine(topic.FinishSummary).AppendLine();
        lines.AppendLine("| 时间（UTC） | 发件人 → 收件人 | 摘要 | 状态 | 文档路径 |")
            .AppendLine("|---|---|---|---|---|");
        string legacy = Handoffs;
        var rows = Read("""
            SELECT i.created_at,CASE WHEN s.topic_id<>$p0 THEN coalesce(st.name||'/','') ELSE '' END || i.sender,
                   CASE WHEN i.topic_id<>$p0 THEN coalesce(tt.name||'/','') ELSE '' END || coalesce(t.name,i.target),
                   substr(i.text,1,220),i.state,i.document,i.document_ref,i.id
            FROM inbox i LEFT JOIN windows s ON s.id=i.source LEFT JOIN topics st ON st.id=s.topic_id
            LEFT JOIN windows t ON t.id=i.target LEFT JOIN topics tt ON tt.id=i.topic_id
            WHERE i.topic_id=$p0 OR i.source IN (SELECT id FROM windows WHERE topic_id=$p0) ORDER BY i.created_at,i.rowid
            """, row =>
            {
                string? reference = Optional(row, 6);
                string? document = row.GetBoolean(5) ? DocumentPath(legacy, reference ?? row.GetString(7)) : reference;
                string path = document == null ? "—" : Path.GetRelativePath(folder, document).Replace('\\', '/');
                return $"| {Optional(row, 0) ?? "历史时间未记录"} | {Cell(row.GetString(1))} → {Cell(row.GetString(2))} | {Cell(row.GetString(3))} | {row.GetString(4)} | {Cell(path)} |";
            }, topic.Id);
        foreach (var row in rows) lines.AppendLine(row);
        foreach (var row in Read("""
            SELECT p.created_at,p.sender,substr(p.body,1,220)
            FROM chat_post p WHERE p.topic=$p0 AND NOT EXISTS(
                SELECT 1 FROM chat_delivery d JOIN inbox i ON i.id=d.id WHERE d.post=p.id)
            ORDER BY p.created_at,p.rowid
            """, row => $"| {row.GetString(0)} | {Cell(row.GetString(1))} | {Cell(row.GetString(2))} | 群聊 | — |", topic.Id))
            lines.AppendLine(row);
        TopicArchive.Publish(Path.Combine(folder, "collab-log.md"), lines.ToString(), overwrite: true);
    }

    private string? QueueHint(AgentWindow target)
    {
        var topic = Topic(target.TopicId);
        if (target.Online && topic.Status == "active") return null;
        string location = topic.Folder == null ? "当前共用交接目录：" + Handoffs : "话题归档目录：" + topic.Folder;
        return "消息已排队。" + location;
    }
}

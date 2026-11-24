using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Files;

namespace FF.AgentCollaboration.Infrastructure.Storage;

// [01、03、04] 窗口启动配置、Grant 及收件均由同一个本机 SQLite 数据库管理。
internal sealed partial class WorkbenchStore(string root, bool create = false, string? databaseFile = null) : LocalState(databaseFile ?? Path.Combine(root, "workbench.sqlite"), create)
{
    private string LegacyHandoffs => Path.Combine(root, "handoffs");
    private string? handoffDirectory;
    private string Handoffs => handoffDirectory ??= Read("SELECT value FROM settings WHERE key='handoffs'", row => row.GetString(0)).SingleOrDefault() ?? LegacyHandoffs;

    internal void Initialize(string? requestedProject, string handoffs, string archiveRoot, bool standalone)
    {
        Exec("PRAGMA journal_mode=WAL");
        Atomic(() =>
        {
            var columns = Read("PRAGMA table_info(windows)", row => row.GetString(1));
            bool upgrade = columns.Count != 0 && !columns.Contains("auto_receive");
            if (upgrade) Exec("ALTER TABLE windows RENAME TO chat_windows");
            Exec("""
                CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS windows(
                    id TEXT PRIMARY KEY, name TEXT NOT NULL COLLATE NOCASE, engine TEXT NOT NULL, project TEXT NOT NULL,
                    model TEXT, session TEXT, effort TEXT, mode TEXT, grant_hash TEXT,
                    auto_receive INTEGER NOT NULL DEFAULT 0, online INTEGER NOT NULL DEFAULT 0, closed INTEGER NOT NULL DEFAULT 0
                );
                CREATE UNIQUE INDEX IF NOT EXISTS terminal_open_history ON windows(engine, session) WHERE closed=0 AND session IS NOT NULL;
                CREATE TABLE IF NOT EXISTS inbox(
                    id TEXT PRIMARY KEY, source TEXT NOT NULL, target TEXT NOT NULL, sender TEXT NOT NULL,
                    text TEXT NOT NULL, files TEXT NOT NULL, document INTEGER NOT NULL, return_hint TEXT, state TEXT NOT NULL,
                    document_ref TEXT
                );
                CREATE INDEX IF NOT EXISTS terminal_waiting_inbox ON inbox(target)
                    WHERE state IN ('pending','writing','delivery_uncertain');
                """);
            if (upgrade) Exec("""
                INSERT INTO windows(id,name,engine,project,model,session)
                SELECT id,name,engine,project,model,session FROM chat_windows
                """);
            // [T04–T05] 旧记录沿用自身 ID。新引用记录保存项目路径或托管文档的原始 ID。
            if (!Read("PRAGMA table_info(inbox)", row => row.GetString(1)).Contains("document_ref"))
                Exec("ALTER TABLE inbox ADD COLUMN document_ref TEXT");
            SaveProject(requestedProject ?? Project());
            InitializeTopics();
            Exec("CREATE TABLE IF NOT EXISTS copilot_session_archives(session TEXT PRIMARY KEY)");
            bool customPaths = CustomPaths;
            Exec("INSERT OR REPLACE INTO settings VALUES('standalone_handoffs',$p0)", standalone ? "1" : "0");
            if (!customPaths) SaveArchiveRoot(archiveRoot);
            if (!Read("PRAGMA table_info(inbox)", row => row.GetString(1)).Contains("created_at"))
                Exec("ALTER TABLE inbox ADD COLUMN created_at TEXT");
            if (!Read("PRAGMA table_info(inbox)", row => row.GetString(1)).Contains("acknowledged"))
                Exec("ALTER TABLE inbox ADD COLUMN acknowledged INTEGER NOT NULL DEFAULT 0");
            Exec("CREATE INDEX IF NOT EXISTS inbox_topic_recent ON inbox(topic_id); CREATE INDEX IF NOT EXISTS inbox_source_recent ON inbox(source)");
            if (!customPaths) ConfigureHandoffs(handoffs);
            Exec("DELETE FROM settings WHERE key='backup_source'");
            Exec("UPDATE windows SET grant_hash=NULL, online=0 WHERE online=1 OR grant_hash IS NOT NULL");
            Exec("UPDATE inbox SET state='delivery_uncertain' WHERE state='writing'");
        });
        foreach (var topic in Topics().Where(topic => topic.Folder == null))
            Atomic(() =>
            {
                BindArchive(topic, null);
                if (topic.Status == "finished") WriteCollaborationLog(Topic(topic.Id));
            });
    }

    private bool CustomPaths => Read("SELECT 1 FROM settings WHERE key='custom_paths'", row => row.GetInt32(0)).Count != 0;
    private void SaveArchiveRoot(string directory) =>
        Exec("INSERT OR REPLACE INTO settings(key,value) VALUES('archive_root',$p0)", directory);

    internal void SetPaths(string? archive, string? handoffs)
    {
        string archiveRoot = StorageDirectories.PrepareDirectory(archive), handoffRoot = StorageDirectories.PrepareDirectory(handoffs);
        Atomic(() =>
        {
            ConfigureHandoffs(handoffRoot, includeTopicDocuments: true);
            SaveArchiveRoot(archiveRoot);
            Exec("INSERT OR IGNORE INTO settings VALUES('custom_paths','1')");
        });
    }

    internal (string Archive, string Handoffs) Directories => (ArchiveRoot, Handoffs);

    private void ConfigureHandoffs(string directory, bool includeTopicDocuments = false)
    {
        string previous = Handoffs;
        Directory.CreateDirectory(directory);
        bool sameRoot = ProjectFolders.Same(previous, directory);
        if (sameRoot && !includeTopicDocuments) return;
        // 主动切换时，旧话题中的已登记托管正文也要可从新目录读取；原位置仍有效。
        foreach (string reference in Read("SELECT DISTINCT coalesce(document_ref,id) FROM inbox WHERE document=1", row => row.GetString(0)))
        {
            bool absolute = Path.IsPathFullyQualified(reference);
            if (absolute && !includeTopicDocuments) continue;
            if (!absolute && sameRoot) continue;
            string id = absolute ? Path.GetFileNameWithoutExtension(reference) : reference;
            if (!Guid.TryParseExact(id, "N", out _)) throw new WorkbenchError("托管交接文档编号无效", WorkbenchFailure.Conflict);
            string oldPath = DocumentPath(previous, reference);
            string newPath = DocumentPath(directory, id);
            if (File.Exists(oldPath)) WorkbenchMigration.CopyFile(oldPath, newPath);
            else if (!File.Exists(newPath)) throw new WorkbenchError("已登记的交接文档缺失，目录设置未更改：" + oldPath, WorkbenchFailure.Conflict);
            if (absolute)
            {
                RememberHandoffs(Path.GetDirectoryName(oldPath)!);
                Exec("UPDATE inbox SET document_ref=$p0 WHERE document=1 AND document_ref=$p1", id, reference);
            }
        }
        if (!sameRoot) RememberHandoffs(previous);
        Exec("INSERT INTO settings VALUES('handoffs',$p0) ON CONFLICT(key) DO UPDATE SET value=excluded.value", directory);
        handoffDirectory = directory;
    }

    private void RememberHandoffs(string path) =>
        Exec("INSERT OR IGNORE INTO settings VALUES('handoffs.previous.' || $p0,$p0)", path);
}

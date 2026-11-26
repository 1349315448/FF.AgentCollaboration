using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Files;

namespace FF.AgentCollaboration.Infrastructure.Storage;

internal static class WorkbenchMigration
{
    internal static string? FindSource(string data, string? knowledge, string initial, string? previous, bool pending)
    {
        bool exists = File.Exists(Path.Combine(data, "workbench.sqlite"));
        if (exists && !pending) return null;
        string? cache = knowledge == null ? null : Path.Combine(knowledge, "agent-collaboration", "cache");
        string branded = WorkbenchLocation.LegacyDirectory;
        string legacy = Path.Combine(WorkbenchLocation.LocalData, "LX.AgentCollaboration", "workbench");
        if (pending && (previous == null || !File.Exists(Path.Combine(previous, "workbench.sqlite"))))
            throw new WorkbenchError("待迁移的来源数据库缺失，请恢复原目录后重试");
        if (previous == null && ProjectFolders.Same(data, initial) && !ProjectFolders.Same(data, branded) &&
            !ProjectFolders.Same(data, WorkbenchLocation.DefaultDirectory) &&
            (cache == null || !ProjectFolders.Same(data, cache))) return null;
        return new[] { previous, initial, cache, branded, legacy }.FirstOrDefault(path =>
            path != null && !ProjectFolders.Same(data, path) && File.Exists(Path.Combine(path, "workbench.sqlite")));
    }

    internal static void Import(string old, string data)
    {
        using var owner = LockOldWorkbench(old);
        Backup(old, data, online: false);
    }

    internal static void Backup(string old, string data, bool online, Action<string>? prepare = null)
    {
        string db = Path.Combine(data, "workbench.sqlite");
        using var source = Open(Path.Combine(old, "workbench.sqlite"), SqliteOpenMode.ReadWrite);
        if (File.Exists(db))
        {
            using var existing = Open(db, SqliteOpenMode.ReadWrite);
            using var check = existing.CreateCommand();
            check.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='settings'";
            if (Convert.ToInt32(check.ExecuteScalar()) == 0) throw Occupied();
            check.CommandText = "SELECT value FROM settings WHERE key='backup_source'";
            if (check.ExecuteScalar() is not string origin || !ProjectFolders.Same(origin, old))
            {
                // 回迁只接受上次迁出时留下的原样数据库，目录被独立使用过就拒绝覆盖。
                using var retired = source.CreateCommand();
                retired.CommandText = "SELECT value FROM settings WHERE key=$key";
                retired.Parameters.AddWithValue("$key", RetiredKey(data));
                if (retired.ExecuteScalar() is not string hash || new FileInfo(db + "-wal") is { Exists: true, Length: > 0 } ||
                    hash != Fingerprint(db)) throw Occupied();
            }
        }
        if (!online)
        {
            using var checkpoint = source.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(FULL)";
            using var result = checkpoint.ExecuteReader();
            if (!result.Read() || result.GetInt32(0) != 0 || result.GetInt32(1) != result.GetInt32(2))
                throw new WorkbenchError("旧工作台的 WAL 仍在使用，迁移可在关闭旧实例后重试", WorkbenchFailure.Conflict);
        }

        string staging = Path.Combine(data, ".workbench-" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            using (var snapshot = Open(staging, SqliteOpenMode.ReadWriteCreate))
            {
                source.BackupDatabase(snapshot);
                CopyFederationDocuments(snapshot,old,data);
                string destination = Path.Combine(data, "handoffs");
                string previous = Path.Combine(old, "handoffs");
                if (Directory.Exists(previous))
                {
                    string relative = Path.GetRelativePath(previous, destination);
                    if (!Path.IsPathRooted(relative) && relative.Split(Path.DirectorySeparatorChar)[0] != "..")
                        throw new WorkbenchError("迁移目标不能位于旧交接目录内");
                    CopyTree(previous, destination);
                }
                // P1–P3 允许把共用 handoffs 放到启动器旁。只复制数据库登记的托管文档。
                using var query = snapshot.CreateCommand();
                query.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN ('settings','inbox')";
                if (Convert.ToInt32(query.ExecuteScalar()) == 2)
                {
                    query.CommandText = "SELECT value FROM settings WHERE key='handoffs'";
                    if (query.ExecuteScalar() is string configured && !ProjectFolders.Same(configured, previous))
                    {
                        query.CommandText = "SELECT count(*) FROM pragma_table_info('inbox') WHERE name='document_ref'";
                        bool hasRef = Convert.ToInt32(query.ExecuteScalar()) != 0;
                        query.CommandText = hasRef ? "SELECT DISTINCT coalesce(document_ref,id) FROM inbox WHERE document=1" :
                            "SELECT id FROM inbox WHERE document=1";
                        using var files = query.ExecuteReader();
                        while (files.Read())
                        {
                            string id = files.GetString(0);
                            if (!Guid.TryParseExact(id, "N", out _)) continue;
                            string path = Path.Combine(configured, id + ".md");
                            if (File.Exists(path)) CopyFile(path, Path.Combine(destination, id + ".md"));
                        }
                    }
                }
            }
            // 文件复制中断时正式库尚未发布，再次启动可核对并复用已复制的文件。
            prepare?.Invoke(staging);
            using (var snapshot = Open(staging, SqliteOpenMode.ReadWrite))
            using (var stamp = snapshot.CreateCommand())
            {
                stamp.CommandText = "PRAGMA journal_mode=DELETE";
                stamp.ExecuteScalar();
                // 标记尚未启用的备份，重试只可替换同一来源的快照。
                stamp.CommandText = "CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY,value TEXT NOT NULL); " +
                    "INSERT OR REPLACE INTO settings(key,value) VALUES('backup_source',$old)";
                stamp.Parameters.AddWithValue("$old", old);
                stamp.ExecuteNonQuery();
                if (!online)
                {
                    stamp.CommandText = "INSERT OR REPLACE INTO settings(key,value) VALUES($key,$hash)";
                    stamp.Parameters.AddWithValue("$key", RetiredKey(old));
                    stamp.Parameters.AddWithValue("$hash", Fingerprint(Path.Combine(old, "workbench.sqlite")));
                    stamp.ExecuteNonQuery();
                }
            }
            // 目标锁已持有；若还有独立 SQLite 连接的旁路日志，留待它关闭后重试。
            if (File.Exists(db + "-wal") || File.Exists(db + "-shm") || File.Exists(db + "-journal")) throw Occupied();
            File.Move(staging, db, overwrite: true);
        }
        finally { ClearStaging(staging); }
    }

    private static string RetiredKey(string root) => "retired_db:" + Path.TrimEndingDirectorySeparator(root).ToUpperInvariant();

    private static string Fingerprint(string db)
    {
        using var file = File.Open(db, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    private static WorkbenchError Occupied() => new("目标目录已有其他工作台数据库，请选择空目录", WorkbenchFailure.Conflict);

    internal static FileStream LockOldWorkbench(string root)
    {
        string path = Path.Combine(root, "workbench.lock");
        try
        {
            if (File.Exists(path)) return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException) { throw new WorkbenchError("旧工作台正在使用数据目录，请退出旧实例后再启动迁移", WorkbenchFailure.Conflict); }
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static void ClearStaging(string staging)
    {
        foreach (string suffix in new[] { "", "-wal", "-shm", "-journal" }) File.Delete(staging + suffix);
    }

    private static void CopyTree(string source, string destination)
    {
        TopicArchive.CheckPath(source);
        TopicArchive.CheckPath(destination);
        Directory.CreateDirectory(destination);
        foreach (var item in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            string target = Path.Combine(destination, item.Name);
            if (item is DirectoryInfo) CopyTree(item.FullName, target);
            else CopyFile(item.FullName, target);
        }
    }

    private static void CopyFederationDocuments(SqliteConnection snapshot,string old,string target)
    {
        using var query=snapshot.CreateCommand();
        query.CommandText="SELECT 1 FROM sqlite_master WHERE type='table' AND name='fed_file'";
        if(query.ExecuteScalar()==null) return;
        var from=Path.Combine(old,"federation-inbox");
        var to=Path.Combine(target,"federation-inbox");
        query.CommandText="SELECT relative,bound FROM fed_file";
        var files=new List<(string Relative,string? Bound)>();
        using(var rows=query.ExecuteReader())
            while(rows.Read()) files.Add((rows.GetString(0),rows.IsDBNull(1)?null:rows.GetString(1)));
        foreach(var file in files)
        {
            string source=Path.GetFullPath(file.Relative,from),destination=Path.GetFullPath(file.Relative,to);
            if(!source.StartsWith(from+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) ||
                !destination.StartsWith(to+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                throw new WorkbenchError("跨端文件的登记路径无效");
            if(!File.Exists(source) && file.Bound==null) continue;
            CopyFile(source,destination);
            if(file.Bound!=null)
            {
                using var rebind=snapshot.CreateCommand();
                rebind.CommandText="UPDATE inbox SET document_ref=$path WHERE id=$id AND document=1";
                rebind.Parameters.AddWithValue("$path",destination);
                rebind.Parameters.AddWithValue("$id",file.Bound);
                rebind.ExecuteNonQuery();
            }
        }
    }

    internal static void CopyFile(string source, string target)
    {
        TopicArchive.CheckPath(source);
        TopicArchive.CheckPath(target);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target))
        {
            using var previous = File.OpenRead(source);
            using var existing = File.OpenRead(target);
            if (!SHA256.HashData(previous).SequenceEqual(SHA256.HashData(existing)))
                throw new WorkbenchError("迁移目标已有内容不同的交接文件：" + target, WorkbenchFailure.Conflict);
            return;
        }
        string temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.Copy(source, temp); File.Move(temp, target); }
        finally { File.Delete(temp); }
    }
}

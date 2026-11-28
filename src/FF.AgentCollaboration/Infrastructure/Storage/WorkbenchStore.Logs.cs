using System.Globalization;
using System.Text.Json;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Files;

namespace FF.AgentCollaboration.Infrastructure.Storage;

internal sealed partial class WorkbenchStore
{
    internal int LogCount() => Read("SELECT (SELECT count(*) FROM inbox)+(SELECT count(*) FROM chat_post)", row => row.GetInt32(0)).Single();

    internal object CleanLogs(int? days)
    {
        if (days is not (0 or 7 or 15 or 30)) throw new WorkbenchError("请选择全部、7、15 或 30 天前的日志");
        const string expired = "state<>'writing' AND ($p0 IS NULL OR created_at IS NULL OR julianday(created_at)<julianday($p0))";
        string? cutoff = days == 0 ? null : DateTime.UtcNow.AddDays(-days.Value).ToString("O", CultureInfo.InvariantCulture);
        int deleted = 0, files = 0, retained = 0;
        Atomic(() =>
        {
            // 先提交日志删除和托管文件回收记录。中断或文件占用时下次清理可继续。
            string directory = CurrentHandoffs();
            foreach (var reference in Read($"SELECT DISTINCT coalesce(document_ref,id) FROM inbox WHERE document=1 AND {expired}",
                         row => row.GetString(0), cutoff))
            {
                string path = DocumentPath(directory, reference);
                if (IsManagedLogFile(path, directory)) Exec("INSERT OR IGNORE INTO handoff_cleanup(path) VALUES($p0)", path);
            }
            deleted = Exec("DELETE FROM inbox WHERE " + expired, cutoff);
            deleted += Exec("""
                DELETE FROM chat_post WHERE ($p0 IS NULL OR julianday(created_at)<julianday($p0))
                AND NOT EXISTS(SELECT 1 FROM chat_delivery d JOIN inbox i ON d.id=i.id WHERE d.post=chat_post.id AND i.state='writing')
                """, cutoff);
            Exec("DELETE FROM chat_delivery WHERE NOT EXISTS(SELECT 1 FROM chat_post p WHERE p.id=chat_delivery.post)");
        });
        Atomic(() =>
        {
            var candidates = Read("SELECT path FROM handoff_cleanup", row => row.GetString(0));
            if (candidates.Count == 0) return;
            // 持有写事务直到删除完毕，其他连接不能在检查引用后插入重发或附件。
            string directory = CurrentHandoffs();
            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Read("SELECT DISTINCT document,coalesce(document_ref,id) FROM inbox WHERE document=1 OR document_ref IS NOT NULL",
                         row => row.GetBoolean(0) ? DocumentPath(directory, row.GetString(1)) : row.GetString(1)))
                referenced.Add(path);
            foreach (var paths in Read("SELECT files FROM inbox WHERE files<>'[]' UNION SELECT files FROM chat_post WHERE files<>'[]'", row => row.GetString(0)))
                referenced.UnionWith(JsonSerializer.Deserialize<string[]>(paths)!);
            foreach (var path in candidates)
            {
                if (referenced.Contains(path)) continue;
                if (IsManagedLogFile(path, directory))
                {
                    try
                    {
                        TopicArchive.CheckPath(path);
                        File.GetAttributes(path);
                        File.Delete(path);
                        files++;
                    }
                    catch (FileNotFoundException) { }
                    catch (DirectoryNotFoundException) { }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or WorkbenchError)
                    {
                        retained++;
                        continue;
                    }
                }
                Exec("DELETE FROM handoff_cleanup WHERE path=$p0", path);
            }
        });
        return new { deleted, files, retained, total = LogCount() };
    }

    // 只释放当前配置的托管目录中由工作台生成的 GUID.md。话题归档树中的原件保留。
    private static bool IsManagedLogFile(string path, string directory) =>
        Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _) &&
        Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase) &&
        ProjectFolders.Same(Path.GetDirectoryName(path)!, directory);
}

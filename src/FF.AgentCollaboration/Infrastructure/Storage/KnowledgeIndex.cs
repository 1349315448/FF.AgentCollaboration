using System.Text.Json;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Files;
using Microsoft.Data.Sqlite;

namespace FF.AgentCollaboration.Infrastructure.Storage;

// Markdown 是源数据，本表可丢弃。以文件时间和大小同步，FTS5 仅索引真正发生变化的文档。
internal sealed class KnowledgeIndex : IDisposable
{
    private const string Schema = """
        PRAGMA journal_mode=MEMORY;
        PRAGMA trusted_schema=OFF;
        CREATE TABLE IF NOT EXISTS documents(
            path TEXT NOT NULL UNIQUE, modified INTEGER NOT NULL, size INTEGER NOT NULL, scope TEXT NOT NULL,
            topic_id TEXT, agent_id TEXT, title TEXT NOT NULL, theme TEXT, keywords TEXT NOT NULL,
            owner TEXT, status TEXT, updated_at TEXT NOT NULL
        );
        CREATE VIRTUAL TABLE IF NOT EXISTS knowledge_fts USING fts5(title, theme, keywords, owner, body, tokenize='trigram');
        """;

    private readonly SqliteConnection database;

    private KnowledgeIndex(string path)
    {
        database = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 5
        }.ToString());
        try
        {
            database.Open();
            using var command = Command(Schema);
            command.ExecuteNonQuery();
            using var verify = Command("SELECT rowid FROM knowledge_fts LIMIT 1");
            verify.ExecuteScalar();
        }
        catch { database.Dispose(); throw; }
    }

    private SqliteCommand Command(string sql, params (string Key, object? Value)[] parameters)
    {
        var command = database.CreateCommand();
        command.CommandText = sql;
        foreach (var (key, value) in parameters) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return command;
    }

    internal static T Open<T>(KnowledgeFiles files, Func<KnowledgeIndex, T> operation)
    {
        string directory = files.IndexDirectory;
        KnowledgeFiles.EnsureDirectory(directory);
        using var guard = WorkspaceDirectoryGuard.Open(directory);
        string path = Path.Combine(directory, "knowledge.db");
        foreach (var suffix in new[] { "", "-journal", "-wal", "-shm" }) TopicArchive.CheckPath(path + suffix);
        T Run()
        {
            using var handle = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            WorkspaceDirectoryGuard.VerifyFile(handle, path);
            // macOS 上验证句柄的文件锁会妨碍 SQLite 初始化，索引目录仍由目录句柄保护。
            if (OperatingSystem.IsMacOS()) handle.Dispose();
            using var index = new KnowledgeIndex(path);
            return operation(index);
        }
        try { return Run(); }
        catch (SqliteException error) when (error.SqliteErrorCode is 11 or 26)
        {
            // Db 及其 sidecars 均为这个索引专用。路径在有目录句柄保护的情况下重新核对。
            foreach (var suffix in new[] { "", "-journal", "-wal", "-shm" })
            {
                string item = path + suffix;
                TopicArchive.CheckPath(item);
                File.Delete(item);
            }
            return Run();
        }
    }

    private void Transaction(Action changes)
    {
        using var begin = Command("BEGIN IMMEDIATE");
        begin.ExecuteNonQuery();
        try
        {
            changes();
            using var commit = Command("COMMIT");
            commit.ExecuteNonQuery();
        }
        catch
        {
            using var rollback = Command("ROLLBACK");
            rollback.ExecuteNonQuery();
            throw;
        }
    }

    internal void Update(KnowledgeDocument document)
    {
        using var check = Command("""
            SELECT 1 FROM documents WHERE path=$path AND modified=$modified AND size=$size
                AND topic_id IS $topic AND agent_id IS $agent
            """, ("$path", document.Path), ("$modified", document.Modified.Ticks), ("$size", document.Size),
            ("$topic", document.Frontmatter.TopicId), ("$agent", document.Frontmatter.AgentId));
        if (check.ExecuteScalar() == null) Transaction(() => Upsert(document));
    }

    internal void Synchronize(KnowledgeFiles files, Func<string, (string Scope, string? TopicId, string? AgentId)?> identify, CancellationToken ct)
    {
        var previous = new Dictionary<string, (long Modified, long Size, string? TopicId, string? AgentId)>(StringComparer.Ordinal);
        using (var command = Command("SELECT path,modified,size,topic_id,agent_id FROM documents"))
        using (var rows = command.ExecuteReader())
            while (rows.Read())
                previous.Add(rows.GetString(0), (rows.GetInt64(1), rows.GetInt64(2), Optional(rows, 3), Optional(rows, 4)));
        var changed = new List<(string Path, string Scope, string? TopicId, string? AgentId)>();
        foreach (var file in files.Scan(ct))
        {
            if (identify(file.Path) is not { } address) continue;
            if (previous.Remove(file.Path, out var stamp) && stamp == (file.Modified.Ticks, file.Size, address.TopicId, address.AgentId)) continue;
            changed.Add((file.Path, address.Scope, address.TopicId, address.AgentId));
        }
        if (previous.Count == 0 && changed.Count == 0) return;
        Transaction(() =>
        {
            foreach (string missing in previous.Keys) Remove(missing);
            foreach (var file in changed)
            {
                ct.ThrowIfCancellationRequested();
                KnowledgeDocument document;
                try { document = files.Read(file.Path, file.Scope, file.TopicId, file.AgentId); }
                catch (Exception error) when (error is WorkbenchError or FileNotFoundException or DirectoryNotFoundException)
                {
                    // 格式错误、移动或冒充链接的文件不能保留旧索引正文。
                    Remove(file.Path);
                    continue;
                }
                Upsert(document);
            }
        });
    }

    private static string? Optional(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);

    private void Remove(string path)
    {
        using var command = Command("""
            DELETE FROM knowledge_fts WHERE rowid=(SELECT rowid FROM documents WHERE path=$path);
            DELETE FROM documents WHERE path=$path
            """, ("$path", path));
        command.ExecuteNonQuery();
    }

    private void Upsert(KnowledgeDocument doc)
    {
        var meta = doc.Frontmatter;
        Remove(doc.Path);
        using var command = Command("""
            INSERT INTO documents(path,modified,size,scope,topic_id,agent_id,title,theme,keywords,owner,status,updated_at)
            VALUES($path,$modified,$size,$scope,$topic,$agent,$title,$theme,$keywords,$owner,$status,$updated);
            INSERT INTO knowledge_fts(rowid,title,theme,keywords,owner,body)
            VALUES(last_insert_rowid(),$title,$theme,$terms,$owner,$body)
            """, ("$path", doc.Path), ("$modified", doc.Modified.Ticks), ("$size", doc.Size), ("$scope", meta.Scope), ("$topic", meta.TopicId),
            ("$agent", meta.AgentId), ("$title", meta.Title), ("$theme", meta.Theme), ("$keywords", JsonSerializer.Serialize(meta.Keywords)),
            ("$owner", meta.Owner), ("$status", meta.Status), ("$updated", meta.Updated), ("$terms", string.Join(' ', meta.Keywords)), ("$body", doc.Body));
        command.ExecuteNonQuery();
    }

    internal KnowledgeHit[] Search(KnowledgeQuery query, AgentWindow? caller, string? recordsPrefix)
    {
        var predicates = new List<string>
        {
            "($scope IS NULL OR d.scope=$scope)",
            "($topic IS NULL OR d.scope='project' OR d.topic_id=$topic)",
            "($agent IS NULL OR d.agent_id=$agent)",
            "($caller IS NULL OR d.scope!='agent' OR d.agent_id=$caller)",
            "($theme IS NULL OR instr(lower(d.theme), lower($theme))>0)"
        };
        var parameters = new List<(string, object?)>
        {
            ("$scope",query.Scope), ("$topic",query.TopicId), ("$agent",query.AgentId), ("$caller",caller?.Id),
            ("$theme",query.Theme), ("$limit",Math.Clamp(query.Limit ?? 50, 1, 100)), ("$offset", query.Offset)
        };
        if (recordsPrefix != null)
        {
            predicates.Add("d.scope='topic' AND substr(d.path,1,length($records))=$records COLLATE NOCASE");
            parameters.Add(("$records", recordsPrefix));
        }
        string[] terms = (query.Query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        string[] fts = terms.Where(term => term.EnumerateRunes().Count() >= 3).ToArray();
        if (fts.Length > 0)
        {
            predicates.Add("knowledge_fts MATCH $match");
            parameters.Add(("$match", string.Join(" AND ", fts.Select(term => "\"" + term.Replace("\"", "\"\"") + "\""))));
        }
        foreach (string term in terms.Except(fts))
        {
            string name = "$term" + parameters.Count;
            predicates.Add($"instr(lower(coalesce(f.title,'')||' '||coalesce(f.theme,'')||' '||coalesce(f.keywords,'')||' '||coalesce(f.owner,'')||' '||f.body),lower({name}))>0");
            parameters.Add((name, term));
        }
        foreach (string tag in query.Keywords ?? [])
        {
            string name = "$tag" + parameters.Count;
            predicates.Add($"EXISTS(SELECT 1 FROM json_each(d.keywords) WHERE value={name} COLLATE NOCASE)");
            parameters.Add((name, tag));
        }

        string fragment = fts.Length == 0 ? "substr(f.body,1,240)" : "snippet(knowledge_fts,-1,'','',' … ',20)";
        string order = fts.Length == 0 ? "" : "bm25(knowledge_fts),";
        if (recordsPrefix == null) order += "d.updated_at DESC,";
        using var command = Command($"""
            SELECT d.path,d.scope,d.topic_id,d.agent_id,d.title,d.theme,d.keywords,d.owner,d.status,{fragment},d.updated_at
            FROM documents d JOIN knowledge_fts f ON f.rowid=d.rowid
            WHERE {string.Join(" AND ",predicates)}
            ORDER BY {order} d.path LIMIT $limit OFFSET $offset
            """, parameters.ToArray());
        using var rows = command.ExecuteReader();
        var result = new List<KnowledgeHit>();
        while (rows.Read())
        {
            string snippet = Optional(rows,9) ?? "";
            result.Add(new(rows.GetString(0), rows.GetString(1), Optional(rows,2), Optional(rows,3), rows.GetString(4),
                Optional(rows,5), JsonSerializer.Deserialize<string[]>(rows.GetString(6)) ?? [], Optional(rows,7), Optional(rows,8),
                snippet[..Math.Min(snippet.Length,240)], rows.GetString(10)));
        }
        return result.ToArray();
    }

    public void Dispose() => database.Dispose();
}

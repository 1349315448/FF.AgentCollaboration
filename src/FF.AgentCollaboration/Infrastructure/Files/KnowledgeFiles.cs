using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FF.AgentCollaboration.Domain;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace FF.AgentCollaboration.Infrastructure.Files;

internal sealed class KnowledgeFiles(string project)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const int FrontmatterLimit = 16 * 1024;
    internal const int Limit = 512 * 1024;
    internal const string Folder = ".ff-acp-knowledge";
    internal string Root { get; } = Path.Combine(project, Folder);

    internal static string Scope(string? value)
    {
        if (value is "project" or "topic" or "agent") return value;
        throw new WorkbenchError("请选择 project、topic 或 agent 知识库层级");
    }

    internal string MarkdownPath(string path)
    {
        path = path.Replace('\\', '/');
        if (path.Length > 512 || !path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
            path.Split('/').Any(part => part.StartsWith('.') || part.Any(char.IsControl)))
            throw new WorkbenchError("请选择知识库内的 Markdown 相对路径", WorkbenchFailure.Forbidden);
        _ = new ProjectFileBrowser(Root).Resolve(path);
        return path;
    }

    // 相对路径使用 / 以便跨平台共享。校验 Windows 文件句柄时则使用本机规范路径。
    private string FullMarkdownPath(string path) => Path.GetFullPath(Path.Combine(Root, MarkdownPath(path)));

    internal string ScopeDirectory(string collection, string id, string name, bool create)
    {
        string parent = Path.Combine(Root, collection);
        using var guard = WorkspaceDirectoryGuard.Open(parent);
        string suffix = "-" + id[..6];
        var matches = new DirectoryInfo(parent).EnumerateDirectories("*", new EnumerationOptions { AttributesToSkip = 0 })
            .Where(folder => folder.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        if (matches.Length > 1) throw new WorkbenchError("知识库短编号有重复目录，请先核对目录归属", WorkbenchFailure.Conflict);
        string slug = string.Concat(name.Select(c => char.IsLetterOrDigit(c) ? c : '-')).Trim('-');
        if (slug.Length > 60) slug = slug[..60].TrimEnd('-');
        string directory = matches.SingleOrDefault()?.Name ?? (slug.Length == 0 ? collection : slug) + suffix;
        string path = Path.Combine(parent, directory);
        TopicArchive.CheckPath(path);
        if (create) EnsureDirectory(path);
        return collection + "/" + directory;
    }

    internal IEnumerable<(string Path, DateTime Modified, long Size)> Scan(CancellationToken ct)
    {
        var directories = new Stack<string>(new[] { "project", "topics", "agents" }.Select(section => Path.Combine(Root, section)));
        int visited = 0;
        while (directories.TryPop(out string? path))
        {
            using var guard = WorkspaceDirectoryGuard.Open(path);
            foreach (var item in new DirectoryInfo(path).EnumerateFileSystemInfos("*", new EnumerationOptions
                { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }))
            {
                ct.ThrowIfCancellationRequested();
                if (++visited > 30_000) throw new WorkbenchError("知识库文件超过索引扫描上限", WorkbenchFailure.Unavailable);
                if (item.Name.StartsWith('.')) continue;
                if (item is DirectoryInfo) directories.Push(item.FullName);
                else if (item is FileInfo file && file.Length <= Limit && file.Extension.Equals(".md", StringComparison.OrdinalIgnoreCase))
                    yield return (Path.GetRelativePath(Root, file.FullName).Replace('\\', '/'), file.LastWriteTimeUtc, file.Length);
            }
        }
    }

    internal KnowledgeDocument Read(string path, string scope, string? topicId, string? agentId)
    {
        string fullPath = FullMarkdownPath(path);
        var file = ReadText(fullPath);
        var (meta, body) = Parse(file.Text);
        string title = Field(meta, "title") ?? body.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith("# "))?[2..]
            ?? Path.GetFileNameWithoutExtension(path);
        return new(path, file.Text, body, new(
            Field(meta, "id") ?? Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(path)).AsSpan(0, 12)),
            scope, topicId, agentId, title, Field(meta, "theme"), ReadKeywords(meta), Field(meta, "owner"), Field(meta, "status"),
            Time(Field(meta, "updated"), file.Modified)), file.Version) { Modified = file.Modified, Size = file.Size };
    }

    internal bool Exists(string path)
    {
        string fullPath = FullMarkdownPath(path);
        TopicArchive.CheckPath(fullPath);
        return File.Exists(fullPath);
    }

    internal KnowledgeDocument Write(string path, string scope, string? topicId, string? agentId, string? owner,
        KnowledgeWrite input, bool onlyCreate = false)
    {
        string dest = FullMarkdownPath(path);
        string parent = Path.GetDirectoryName(dest)!;
        EnsureDirectory(parent);
        using var guard = WorkspaceDirectoryGuard.Open(parent);
        TopicArchive.CheckPath(dest);
        var previous = File.Exists(dest) ? Read(path, scope, topicId, agentId) : null;
        if (onlyCreate && previous != null) return previous;
        if (input.ExpectedVersion != null && input.ExpectedVersion != previous?.Version)
            throw new WorkbenchError("知识库文档已更新，请重新读取后保存。草稿保留在编辑框中", WorkbenchFailure.Conflict);
        string content = input.Content ?? throw new WorkbenchError("请填写知识库正文");
        ValidateText(content);
        var (meta, body) = Parse(content);
        string? theme = MetadataValue(input.Theme ?? Field(meta, "theme") ?? previous?.Frontmatter.Theme, 100);
        string? status = input.Status ?? Field(meta, "status") ?? previous?.Frontmatter.Status;
        if (status != null && status is not ("planned" or "working" or "done" or "blocked"))
            throw new WorkbenchError("知识库状态应为 planned、working、done 或 blocked");
        string[] keywords = input.Keywords ?? (meta != null ? ReadKeywords(meta) : previous?.Frontmatter.Keywords) ?? [];
        if (keywords.Length > 30 || keywords.Any(tag => string.IsNullOrWhiteSpace(tag)))
            throw new WorkbenchError("每篇知识文档最多 30 个非空关键词");
        keywords = keywords.Select(tag => MetadataValue(tag, 80)!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var fields = new YamlMappingNode();
        void Add(string key, string? value)
        {
            if (value != null) fields.Add(key, new YamlScalarNode(value) { Style = ScalarStyle.DoubleQuoted });
        }
        Add("id", previous?.Frontmatter.Id ?? Guid.NewGuid().ToString("N"));
        Add("scope", scope);
        Add("topicId", topicId);
        Add("agentId", agentId);
        Add("owner", owner ?? MetadataValue(Field(meta, "owner") ?? previous?.Frontmatter.Owner, 100));
        Add("title", MetadataValue(input.Title ?? Field(meta, "title") ?? previous?.Frontmatter.Title, 200));
        Add("theme", theme);
        fields.Add("keywords", new YamlSequenceNode(keywords.Select(tag => new YamlScalarNode(tag) { Style = ScalarStyle.DoubleQuoted })));
        Add("status", status);
        Add("updated", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        using var yaml = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\n" };
        new YamlStream(new YamlDocument(fields)).Save(yaml, assignAnchors: false);
        string frontmatter = yaml.ToString().TrimEnd('\r', '\n');
        if (frontmatter.EndsWith("\n...", StringComparison.Ordinal)) frontmatter = frontmatter[..^4];
        if (frontmatter.StartsWith("---\n", StringComparison.Ordinal)) frontmatter = frontmatter[4..];
        string markdown = "---\n" + frontmatter + "\n---\n" + body;
        Publish(dest, markdown, previous?.Version, verifyVersion: true);
        return Read(path, scope, topicId, agentId);
    }

    private static string? MetadataValue(string? value, int limit) =>
        value == null ? null : WorkbenchInput.Text(value, "知识文档元数据", limit);

    private static string Time(string? updated, DateTime fallback) =>
        DateTimeOffset.TryParse(updated, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) : fallback.ToString("O", CultureInfo.InvariantCulture);

    private static string? Field(YamlMappingNode? map, string key) =>
        map != null && map.Children.TryGetValue(new YamlScalarNode(key), out var value) ? (value as YamlScalarNode)?.Value : null;

    private static string[] ReadKeywords(YamlMappingNode? map) =>
        map != null && map.Children.TryGetValue(new YamlScalarNode("keywords"), out var node) && node is YamlSequenceNode sequence
            ? sequence.Children.OfType<YamlScalarNode>().Select(tag => tag.Value ?? "").Where(tag => tag.Length is > 0 and <= 80).Take(30).ToArray() : [];

    private static (YamlMappingNode? Meta, string Body) Parse(string text)
    {
        if (!(text.StartsWith("---\n", StringComparison.Ordinal) || text.StartsWith("---\r\n", StringComparison.Ordinal))) return (null, text);
        int start = text.IndexOf('\n') + 1;
        int end = start;
        while (end < text.Length && end - start <= FrontmatterLimit)
        {
            int next = text.IndexOf('\n', end);
            if (next < 0) next = text.Length;
            if (text.AsSpan(end, next - end).TrimEnd('\r').SequenceEqual("---"))
            {
                try
                {
                    using var reader = new StringReader(text[start..end]);
                    var parser = new Parser(reader);
                    // 只接收浅层标量和关键词序列，阻止别名、复杂 YAML 结构进入索引。
                    int depth = 0;
                    while (parser.MoveNext())
                    {
                        if (parser.Current is YamlDotNet.Core.Events.AnchorAlias)
                            throw new WorkbenchError("知识库 Frontmatter 不支持 YAML 别名");
                        if (parser.Current is YamlDotNet.Core.Events.MappingStart or YamlDotNet.Core.Events.SequenceStart && ++depth > 2)
                            throw new WorkbenchError("知识库 Frontmatter 仅支持简单字段与关键词列表");
                        if (parser.Current is YamlDotNet.Core.Events.MappingEnd or YamlDotNet.Core.Events.SequenceEnd) depth--;
                    }
                    var yaml = new YamlStream();
                    yaml.Load(new StringReader(text[start..end]));
                    if (yaml.Documents.Count == 1 && yaml.Documents[0].RootNode is YamlMappingNode map)
                        return (map, text[Math.Min(next + 1, text.Length)..]);
                }
                catch (YamlException) { throw new WorkbenchError("请检查知识库文档的 YAML Frontmatter"); }
                throw new WorkbenchError("知识库 Frontmatter 必须是 YAML 映射");
            }
            end = next + 1;
        }
        throw new WorkbenchError("知识库 Frontmatter 缺少结束标记或超过 16 KiB");
    }

    internal static void EnsureDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            using var existing = WorkspaceDirectoryGuard.Open(path);
            return;
        }
        string parent = Path.GetDirectoryName(path)!;
        EnsureDirectory(parent);
        using var guard = WorkspaceDirectoryGuard.Open(parent);
        TopicArchive.CheckPath(path);
        Directory.CreateDirectory(path);
        using var created = WorkspaceDirectoryGuard.Open(path);
    }

    internal static (string Text, DateTime Modified, string Version, long Size) ReadText(string path)
    {
        using var guard = WorkspaceDirectoryGuard.Open(Path.GetDirectoryName(path)!);
        TopicArchive.CheckPath(path);
        if (!File.Exists(path)) throw new WorkbenchError("知识库文档不存在", WorkbenchFailure.NotFound);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        WorkspaceDirectoryGuard.VerifyFile(file.SafeFileHandle, path);
        if (file.Length > Limit) throw new WorkbenchError("知识库单篇文档上限为 512 KiB");
        var bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        try
        {
            var text = Utf8.GetString(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? bytes.AsSpan(3) : bytes.AsSpan());
            ValidateText(text);
            return (text, File.GetLastWriteTimeUtc(file.SafeFileHandle), Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.LongLength);
        }
        catch (DecoderFallbackException) { throw new WorkbenchError("知识库仅支持有效的 UTF-8 文档"); }
    }

    private static void ValidateText(string text)
    {
        if (text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            throw new WorkbenchError("知识库文档包含不可保存的控制字符");
        try
        {
            if (Utf8.GetByteCount(text) > Limit) throw new WorkbenchError("知识库单篇文档上限为 512 KiB");
        }
        catch (EncoderFallbackException) { throw new WorkbenchError("知识库仅支持有效的 UTF-8 文档"); }
    }

    internal static void Publish(string dest, string content, string? expectedVersion, bool verifyVersion)
    {
        ValidateText(content);
        string parent = Path.GetDirectoryName(dest)!;
        using var guard = WorkspaceDirectoryGuard.Open(parent);
        TopicArchive.CheckPath(dest);
        string temporary = Path.Combine(parent, ".ff-kb-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(Utf8.GetBytes(content));
                file.Flush(true);
            }
            if (File.Exists(dest))
            {
                using var original = File.OpenHandle(dest, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                WorkspaceDirectoryGuard.VerifyFile(original, dest);
                if (verifyVersion && (expectedVersion == null || ReadText(dest).Version != expectedVersion))
                    throw new WorkbenchError("知识库文件已变化，请重新读取后保存", WorkbenchFailure.Conflict);
                File.Replace(temporary, dest, null);
            }
            else
            {
                if (verifyVersion && expectedVersion != null)
                    throw new WorkbenchError("知识库文件已被移除，请刷新", WorkbenchFailure.Conflict);
                File.Move(temporary, dest);
            }
        }
        finally { File.Delete(temporary); }
    }
}

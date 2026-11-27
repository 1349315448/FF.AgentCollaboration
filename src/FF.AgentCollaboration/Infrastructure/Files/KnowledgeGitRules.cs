using FF.AgentCollaboration.Domain;

namespace FF.AgentCollaboration.Infrastructure.Files;

internal static class KnowledgeGitRules
{
    private const string Marker = "# FF.ACP local knowledge";

    internal static void Configure(string project, string knowledgeRoot, bool localOnly)
    {
        string indexRule = Path.Combine(knowledgeRoot, ".gitignore");
        var original = Read(indexRule);
        if (!Lines(original.Text).Any(line => line is ".index/" or "/.index/"))
            KnowledgeFiles.Publish(indexRule, Append(original.Text, ".index/"), original.Version, verifyVersion: true);

        for (var directory = new DirectoryInfo(project); directory != null; directory = directory.Parent)
        {
            string git = Path.Combine(directory.FullName, ".git");
            TopicArchive.CheckPath(git);
            if (!Path.Exists(git)) continue;
            string metadata = Directory.Exists(git) ? git : LinkedMetadata(git);
            string info = Path.Combine(metadata, "info");
            string exclude = Path.Combine(info, "exclude");
            if (!localOnly && !File.Exists(exclude)) return;
            KnowledgeFiles.EnsureDirectory(info);
            string rule = "/" + Path.GetRelativePath(directory.FullName, knowledgeRoot).Replace('\\', '/') + "/";
            // Git 路径模式中的字符必须按字面转义。
            rule = string.Concat(rule.Select(c => c is '*' or '?' or '[' or ']' or ' ' ? "\\" + c : c.ToString()));
            UpdateExclude(exclude, rule, localOnly);
            return;
        }
    }

    private static string LinkedMetadata(string git)
    {
        string pointer = KnowledgeFiles.ReadText(git).Text.Trim();
        if (!pointer.StartsWith("gitdir: ", StringComparison.Ordinal) || pointer.Contains('\n'))
            throw new WorkbenchError("无法安全识别这个 Git 工作树的元数据");
        string metadata = Path.GetFullPath(pointer[8..], Path.GetDirectoryName(git)!);
        using var guard = WorkspaceDirectoryGuard.Open(metadata);
        string common = Path.Combine(metadata, "commondir");
        if (!File.Exists(common)) return metadata;
        // linked worktree 带有返指当前目录的 gitdir 文件，只采用 Git 标准的共享目录结构。
        string backlink = Path.GetFullPath(KnowledgeFiles.ReadText(Path.Combine(metadata, "gitdir")).Text.Trim(), metadata);
        string shared = Path.GetFullPath(KnowledgeFiles.ReadText(common).Text.Trim(), metadata);
        string worktrees = Path.GetDirectoryName(metadata)!;
        if (!ProjectFolders.Same(backlink, git) || Path.GetFileName(worktrees) != "worktrees" ||
            !ProjectFolders.Same(Path.GetDirectoryName(worktrees)!, shared))
            throw new WorkbenchError("无法安全识别这个 Git 工作树的公用目录");
        return shared;
    }

    private static void UpdateExclude(string path, string rule, bool localOnly)
    {
        var original = Read(path);
        var lines = Lines(original.Text).ToList();
        for (int i = 0; i + 1 < lines.Count; i++)
        {
            if (lines[i] != Marker || lines[i + 1] != rule) continue;
            if (localOnly) return;
            lines.RemoveRange(i, 2);
            string newline = original.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            KnowledgeFiles.Publish(path, string.Join(newline, lines), original.Version, verifyVersion: true);
            return;
        }
        // 尊重仓库已存在的同等忽略项，切回共享时只移除工作台写入的行。
        if (localOnly && !lines.Any(line => line == rule || line == rule[1..]))
            KnowledgeFiles.Publish(path, Append(original.Text, Marker, rule), original.Version, verifyVersion: true);
    }

    private static string[] Lines(string text) => text.Replace("\r\n", "\n").Split('\n');

    private static string Append(string text, params string[] lines)
    {
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return text + (text.Length == 0 || text.EndsWith('\n') ? "" : newline) + string.Join(newline, lines) + newline;
    }

    private static (string Text, string? Version) Read(string file)
    {
        TopicArchive.CheckPath(file);
        if (!File.Exists(file)) return ("", null);
        var current = KnowledgeFiles.ReadText(file);
        return (current.Text, current.Version);
    }
}

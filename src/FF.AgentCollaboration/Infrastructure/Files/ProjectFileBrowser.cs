using System.Text;
using FF.AgentCollaboration.Domain;

namespace FF.AgentCollaboration.Infrastructure.Files;

internal sealed record WorkspaceEntry(string Name, string Path, bool Directory);
internal sealed record WorkspaceFile(string Path, string Kind, DateTime MtimeUtc, long Size, string? Content = null);
internal sealed record WorkspaceEdit(string Project, string Path, string Content, DateTimeOffset? ExpectedMtimeUtc);

// 只接受宿主已登记的根。交接文档由消息授权入口解析，不给文件树扩展目录权限。
internal sealed class ProjectFileBrowser(string root)
{
    internal const int PreviewLimit = 20 * 1024 * 1024;
    private const int VideoPreviewLimit = 100 * 1024 * 1024;
    internal const int Limit = 512 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly object SaveGate = new();
    private static readonly HashSet<string> Ignored = new([".git", "node_modules", "bin", "obj", "dist"], StringComparer.OrdinalIgnoreCase);

    internal string Resolve(string? relative, bool allowRoot = false)
    {
        if (string.IsNullOrEmpty(relative) && allowRoot) return root;
        var parts = (relative ?? "").Replace('\\', '/').Split('/');
        foreach (string part in parts)
        {
            string stem = part.Split('.')[0].TrimEnd().ToUpperInvariant();
            if (part.Length == 0 || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || part.Contains(':') || stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
                || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && "0123456789¹²³".Contains(stem[3])))
                throw new WorkbenchError("请选择项目内的相对路径", WorkbenchFailure.Forbidden);
        }
        return Path.Combine(root, Path.Combine(parts));
    }

    private string Relative(string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    private IEnumerable<FileSystemInfo> Children(string directory, bool showIgnored)
    {
        using var guard = WorkspaceDirectoryGuard.Open(directory);
        if (!Directory.Exists(directory)) throw Missing();
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", new EnumerationOptions {
            IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System }))
        {
            if (showIgnored || entry is not DirectoryInfo || !Ignored.Contains(entry.Name)) yield return entry;
        }
    }

    internal IReadOnlyList<WorkspaceEntry> Tree(string? path, bool showIgnored, CancellationToken ct) =>
        Children(Resolve(path, true), showIgnored).Select(entry => {
            ct.ThrowIfCancellationRequested();
            return new WorkspaceEntry(entry.Name, Relative(entry.FullName), entry is DirectoryInfo);
        }).OrderByDescending(e => e.Directory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    internal ProjectFileListing Search(string? query, bool showIgnored, CancellationToken ct)
    {
        var files = new List<string>();
        if (string.IsNullOrWhiteSpace(query)) return new(files, false);
        string keyword = query.Trim();
        var remaining = new Stack<string>();
        remaining.Push(root);
        int scanned = 0;
        while (remaining.TryPop(out var directory))
        {
            try
            {
                foreach (var entry in Children(directory, showIgnored))
                {
                    ct.ThrowIfCancellationRequested();
                    if (++scanned > 30_000) return new(files, true);
                    if (entry is DirectoryInfo) remaining.Push(entry.FullName);
                    else if (entry.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    {
                        if (files.Count == 100) return new(files, true);
                        files.Add(Relative(entry.FullName));
                    }
                }
            }
            catch (Exception error) when (directory != root && error is DirectoryNotFoundException or UnauthorizedAccessException
                or WorkbenchError { Failure: WorkbenchFailure.NotFound or WorkbenchFailure.Forbidden })
            { /* 搜索期间移除或变成链接的子目录不影响其他分支。 */ }
        }
        files.Sort(StringComparer.OrdinalIgnoreCase);
        return new(files, false);
    }

    private static WorkbenchError Missing() => new("文件或目录已不存在", WorkbenchFailure.NotFound);

    private static FileStream OpenVerified(string fullPath)
    {
        using var guard = WorkspaceDirectoryGuard.Open(Path.GetDirectoryName(fullPath)!);
        TopicArchive.CheckPath(fullPath);
        if (!File.Exists(fullPath)) throw Missing();
        var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try { WorkspaceDirectoryGuard.VerifyFile(stream.SafeFileHandle, fullPath); return stream; }
        catch { stream.Dispose(); throw; }
    }

    private static (WorkspaceFile File, Encoding Encoding, int Bom, byte[] Bytes, string? Mime) Inspect(string fullPath, string label)
    {
        using var stream = OpenVerified(fullPath);
        var file = new WorkspaceFile(label, "large", File.GetLastWriteTimeUtc(stream.SafeFileHandle), stream.Length);
        if (file.Size > PreviewLimitFor(label)) return (file, Utf8, 0, [], null);
        var format = WorkspacePreviewFormat.Detect(stream, label);
        if (format != null) return (file with { Kind = format.Value.Kind }, Utf8, 0, [], format.Value.Mime);
        if (file.Size > Limit) return (file, Utf8, 0, [], null);
        var bytes = new byte[(int)file.Size];
        stream.ReadExactly(bytes);
        ReadOnlySpan<byte> data = bytes;
        Encoding encoding = Utf8;
        int bom = data.StartsWith(Encoding.UTF8.Preamble) ? 3 : 0;
        if (data.StartsWith(Encoding.Unicode.Preamble) || data.StartsWith(Encoding.BigEndianUnicode.Preamble))
        {
            encoding = new UnicodeEncoding(data[0] == 0xfe, true, true);
            bom = 2;
        }
        try
        {
            string content = encoding.GetString(data[bom..]);
            if (IsText(content))
                return (file with { Kind = "text", Content = content }, encoding, bom, bytes, null);
        }
        catch (DecoderFallbackException) { /* 字节无法无损往返时仅告知二进制类型。 */ }
        return (file with { Kind = "binary" }, encoding, bom, [], null);
    }

    private static bool IsText(string content) =>
        !content.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t' or '\f'));

    internal WorkspaceFile Read(string path) => Inspect(Resolve(path), path).File;
    internal static WorkspaceFile ReadDocument(string authorizedPath) => Inspect(authorizedPath, Path.GetFileName(authorizedPath)).File;
    private static int PreviewLimitFor(string path) => Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".webm"
        ? VideoPreviewLimit : PreviewLimit;

    internal (FileStream Stream, string Mime) OpenPreview(string path, bool imagesOnly)
    {
        var stream = OpenVerified(Resolve(path));
        try
        {
            int limit = PreviewLimitFor(path);
            if (stream.Length > limit) throw new WorkbenchError($"只读预览上限为 {limit / (1024 * 1024)} MiB");
            var format = WorkspacePreviewFormat.Detect(stream, path);
            if (format == null || (imagesOnly && format.Value.Kind != "image") || format.Value.Kind == "legacy-ppt")
                throw new WorkbenchError("格式与文件内容不匹配或不支持预览");
            return (stream, format.Value.Mime);
        }
        catch { stream.Dispose(); throw; }
    }

    internal WorkspaceFile Save(WorkspaceEdit edit)
    {
        string dest = Resolve(edit.Path);
        if (edit.Content == null || edit.Content.Length > Limit || edit.ExpectedMtimeUtc == null)
            throw new WorkbenchError("保存需要原文件版本，正文不能超过 512 KB");
        if (!IsText(edit.Content)) throw new WorkbenchError("文本不能包含二进制控制字符");
        lock (SaveGate)
        {
            using var guard = WorkspaceDirectoryGuard.Open(Path.GetDirectoryName(dest)!);
            void CheckVersion()
            {
                TopicArchive.CheckPath(dest);
                if (!File.Exists(dest) || File.GetLastWriteTimeUtc(dest) != edit.ExpectedMtimeUtc.Value.UtcDateTime)
                    throw new WorkbenchError("磁盘文件已更新，草稿已保留。请重新读取后核对。", WorkbenchFailure.Conflict);
            }
            CheckVersion();
            var old = Inspect(dest, edit.Path);
            if (old.File.Kind != "text") throw new WorkbenchError("只能保存 512 KB 以内的文本文件");
            if (old.Encoding.GetByteCount(edit.Content) + old.Bom > Limit) throw new WorkbenchError("正文不能超过 512 KB");
            // 在同目录准备完整文件后替换。已有硬链接的其他文件名仍指向原始内容。
            string temp = Path.Combine(Path.GetDirectoryName(dest)!, ".ff-edit-" + Guid.NewGuid().ToString("N"));
            var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            try
            {
                using (output)
                {
                    output.Write(old.Bytes.AsSpan(0, old.Bom));
                    output.Write(old.Encoding.GetBytes(edit.Content));
                    output.Flush(true);
                }
                var next = DateTime.UtcNow;
                File.SetLastWriteTimeUtc(temp, next > old.File.MtimeUtc ? next : old.File.MtimeUtc.AddMilliseconds(1));
                // 版本复核到替换之间阻止常规编辑器原地写入；允许原子替换需要的 delete 共享。
                using var original = File.OpenHandle(dest, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                WorkspaceDirectoryGuard.VerifyFile(original, dest);
                CheckVersion();
                File.Replace(temp, dest, null);
            }
            catch (IOException error) when (error is FileNotFoundException || (error.HResult & 0xffff) is 32 or 33)
            { throw new WorkbenchError("文件正在被其他程序修改，草稿已保留", WorkbenchFailure.Conflict); }
            finally { File.Delete(temp); }
            return Read(edit.Path);
        }
    }
}

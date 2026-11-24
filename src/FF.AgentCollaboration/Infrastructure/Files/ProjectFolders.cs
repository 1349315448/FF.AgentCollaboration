using FF.AgentCollaboration.Domain;

namespace FF.AgentCollaboration.Infrastructure.Files;

internal sealed record FolderEntry(string Name, string Path);
internal sealed record FolderListing(string? Path, string? Parent, IReadOnlyList<FolderEntry> Folders);
internal sealed record ProjectFileListing(IReadOnlyList<string> Files, bool Truncated);

// [01] 工作台只浏览本机文件夹，选定后的路径由宿主作为原生 cwd 传入。
internal static class ProjectFolders
{
    private static readonly HashSet<string> Excluded = new([".git", "bin", "obj", "node_modules"], StringComparer.OrdinalIgnoreCase);
    internal static bool Same(string left, string right) => string.Equals(left, right,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    internal static string Resolve(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new WorkbenchError("请选择一个本机项目文件夹");
        string fullPath;
        try
        {
            fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (OperatingSystem.IsWindows())
            {
                string drive = Path.GetPathRoot(fullPath)!;
                if (drive.Length != 3 || drive[1] != ':' || new DriveInfo(drive).DriveType == DriveType.Network)
                    throw new WorkbenchError("请选择本地磁盘上的文件夹");
            }
        }
        catch (ArgumentException)
        {
            throw new WorkbenchError("文件夹路径格式不正确");
        }
        if (!Directory.Exists(fullPath)) throw new WorkbenchError("该文件夹不存在或无法访问", WorkbenchFailure.NotFound);
        return fullPath;
    }

    internal static FolderListing List(string? path)
    {
        if (path == null)
        {
            var drives = DriveInfo.GetDrives().Where(drive => drive.DriveType != DriveType.Network && drive.IsReady)
                .Select(drive => new FolderEntry(drive.Name, drive.RootDirectory.FullName)).ToArray();
            return new(null, null, drives);
        }
        var directory = new DirectoryInfo(Resolve(path));
        try
        {
            var folders = directory.EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = false })
                .OrderBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase)
                .Select(folder => new FolderEntry(folder.Name, folder.FullName)).ToArray();
            return new(directory.FullName, directory.Parent?.FullName, folders);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new WorkbenchError("这个文件夹无法读取，请选择其他目录", WorkbenchFailure.Forbidden);
        }
    }

    // [02] 逐层枚举，使排除和重解析点检查发生在进入目录前。
    internal static ProjectFileListing Files(string project, string? keyword, CancellationToken ct)
    {
        var matches = new List<string>();
        var remaining = new Stack<DirectoryInfo>();
        remaining.Push(new DirectoryInfo(project));
        var options = new EnumerationOptions
        {
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System,
            IgnoreInaccessible = true
        };
        int scanned = 0;
        while (remaining.TryPop(out var directory))
        {
            foreach (var item in directory.EnumerateFileSystemInfos("*", options))
            {
                ct.ThrowIfCancellationRequested();
                if (++scanned > 30_000) return new(matches, true);
                if (Excluded.Contains(item.Name)) continue;
                if (item is DirectoryInfo child)
                {
                    remaining.Push(child);
                    continue;
                }
                string relative = Path.GetRelativePath(project, item.FullName).Replace('\\', '/');
                if (!string.IsNullOrEmpty(keyword) && !relative.Contains(keyword, StringComparison.OrdinalIgnoreCase)) continue;
                if (matches.Count == 150) return new(matches, true);
                matches.Add(relative);
            }
        }
        matches.Sort(StringComparer.OrdinalIgnoreCase);
        return new(matches, false);
    }

    internal static string Attachment(string project, string input)
    {
        string file = Path.GetFullPath(input, project);
        string relative = Path.GetRelativePath(project, file);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (Path.IsPathRooted(relative) || segments[0] is "." or ".." || !File.Exists(file))
            throw new WorkbenchError("附件必须是发件窗口项目内的普通文件");
        string current = project;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new WorkbenchError("附件路径不能经过链接或重解析点");
        }
        return file;
    }
}

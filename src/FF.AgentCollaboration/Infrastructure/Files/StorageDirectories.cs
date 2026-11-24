using FF.AgentCollaboration.Domain;

namespace FF.AgentCollaboration.Infrastructure.Files;

internal static class StorageDirectories
{
    internal static string PrepareDirectory(string? path)
    {
        if (path == null || !Path.IsPathFullyQualified(path)) throw new WorkbenchError("请选择绝对目录");
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        TopicArchive.CheckPath(full);
        Directory.CreateDirectory(full);
        CheckDirectory(full);
        return full;
    }

    internal static void CheckDirectory(string path)
    {
        TopicArchive.CheckPath(path);
        using var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
        entries.MoveNext();
        using var probe = new FileStream(Path.Combine(path, ".ff-agent-" + Guid.NewGuid().ToString("N")),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        probe.WriteByte(1);
    }
}

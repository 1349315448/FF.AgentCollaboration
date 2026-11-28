using FF.AgentCollaboration.Domain;

namespace FF.AgentCollaboration.Infrastructure.Files;

internal static class StorageDirectories
{
    private const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    internal static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, PrivateDirectory);
    }

    internal static string PrepareDirectory(string? path, bool privateData = false)
    {
        if (path == null || !Path.IsPathFullyQualified(path)) throw new WorkbenchError("请选择绝对目录");
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        TopicArchive.CheckPath(full);
        if (privateData) CreatePrivateDirectory(full);
        else Directory.CreateDirectory(full);
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

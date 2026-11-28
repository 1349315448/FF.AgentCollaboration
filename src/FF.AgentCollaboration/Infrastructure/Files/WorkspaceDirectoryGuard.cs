using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using FF.AgentCollaboration.Domain;
using Microsoft.Win32.SafeHandles;

namespace FF.AgentCollaboration.Infrastructure.Files;

// 在 Windows 上按祖先顺序打开目录本身，并在此次访问结束前禁止重命名或替换这些目录。
internal sealed class WorkspaceDirectoryGuard : IDisposable
{
    private readonly List<SafeFileHandle> handles = [];
    private readonly string directory;
    private UnixWorkspaceDirectory? unix;

    private WorkspaceDirectoryGuard(string directory) => this.directory = directory;

    internal static WorkspaceDirectoryGuard Open(string directory)
    {
        var guard = new WorkspaceDirectoryGuard(directory);
        if (!OperatingSystem.IsWindows())
        {
            guard.unix = UnixWorkspaceDirectory.Open(directory);
            return guard;
        }
        var chain = new Stack<string>();
        for (string? part = directory; part != null; part = Path.GetDirectoryName(part)) chain.Push(part);
        try
        {
            foreach (string part in chain)
            {
                var handle = CreateFileW(part, 0xA0, FileShare.Read | FileShare.Write, IntPtr.Zero,
                    FileMode.Open, 0x02000000 | 0x00200000, IntPtr.Zero);
                guard.handles.Add(handle);
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new WorkbenchError(new Win32Exception(error).Message,
                        error is 2 or 3 ? WorkbenchFailure.NotFound : WorkbenchFailure.Forbidden);
                }
                var attrs = File.GetAttributes(handle);
                if (attrs.HasFlag(FileAttributes.ReparsePoint) || !attrs.HasFlag(FileAttributes.Directory))
                    throw new WorkbenchError("不能经过链接或重解析点访问项目", WorkbenchFailure.Forbidden);
            }
            return guard;
        }
        catch { guard.Dispose(); throw; }
    }

    internal FileStream Read(string path, FileShare share = FileShare.Read)
    {
        string name = ChildName(path);
        if (unix != null) return unix.Read(name);
        TopicArchive.CheckPath(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, share);
        try { VerifyFile(stream.SafeFileHandle, path); return stream; }
        catch { stream.Dispose(); throw; }
    }

    internal FileStream Create(string path, UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite) =>
        unix != null ? unix.Create(ChildName(path), mode) : new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);

    internal void Publish(string temporary, string path, bool replace)
    {
        if (unix != null) unix.Rename(ChildName(temporary), unix, ChildName(path), replace);
        else if (replace) File.Replace(temporary,path,null);
        else File.Move(temporary,path);
    }

    internal void Delete(string path)
    {
        if (unix != null) unix.Delete(ChildName(path));
        else File.Delete(path);
    }

    internal void PublishTo(string temporary, WorkspaceDirectoryGuard target, string path)
    {
        if (unix != null) unix.Rename(ChildName(temporary), target.unix!, target.ChildName(path), replace:false);
        else File.Move(temporary,path);
    }

    internal void EnsureChild(string path)
    {
        if (unix != null) unix.EnsureChild(ChildName(path));
        else Directory.CreateDirectory(path);
    }

    private string ChildName(string path)
    {
        if (!ProjectFolders.Same(Path.GetDirectoryName(path), directory))
            throw new WorkbenchError("文件不在已打开的目录内", WorkbenchFailure.Forbidden);
        return Path.GetFileName(path);
    }

    internal static void VerifyFile(SafeFileHandle handle, string expected)
    {
        if (OperatingSystem.IsWindows())
        {
            var path = new StringBuilder(512);
            uint size = GetFinalPathNameByHandleW(handle, path, (uint)path.Capacity, 0);
            if (size >= path.Capacity)
            {
                path.EnsureCapacity(checked((int)size + 1));
                size = GetFinalPathNameByHandleW(handle, path, (uint)path.Capacity, 0);
            }
            string actual = path.ToString();
            if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
            if (size != 0 && ProjectFolders.Same(actual, expected)) return;
            if (size != 0)
            {
                uint length = GetLongPathNameW(expected, null, 0);
                if (length > 0)
                {
                    var expanded = new StringBuilder(checked((int)length));
                    if (GetLongPathNameW(expected, expanded, length) != 0 && ProjectFolders.Same(actual, expanded.ToString())) return;
                }
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            string? actual = new FileInfo("/proc/self/fd/" + handle.DangerousGetHandle()).LinkTarget;
            if (actual == expected) return;
        }
        else if (OperatingSystem.IsMacOS())
        {
            byte[] result = new byte[1024];
            if (UnixWorkspaceDirectory.DescriptorPath(handle, result) == 0)
            {
                int end = Array.IndexOf(result, (byte)0);
                if (end >= 0 && Encoding.UTF8.GetString(result,0,end) == expected) return;
            }
        }
        throw new WorkbenchError("文件位置已经变化，请刷新目录", WorkbenchFailure.Forbidden);
    }

    public void Dispose()
    {
        unix?.Dispose();
        for (int i = handles.Count - 1; i >= 0; i--) handles[i].Dispose();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, FileShare share, IntPtr security,
        FileMode creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint length, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetLongPathNameW(string path, StringBuilder? result, uint length);
}

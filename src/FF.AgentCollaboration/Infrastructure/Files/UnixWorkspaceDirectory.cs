using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using FF.AgentCollaboration.Domain;

namespace FF.AgentCollaboration.Infrastructure.Files;

// 在打开根目录后逐段使用 openat。文件访问与替换始终相对于持有的目录描述符。
internal sealed class UnixWorkspaceDirectory(SafeFileHandle directory) : IDisposable
{
    private static int NoFollow => OperatingSystem.IsMacOS() ? 0x100 : 0x20000;
    private static int DirectoryOnly => OperatingSystem.IsMacOS() ? 0x100000 : 0x10000;
    private static int CloseOnExec => OperatingSystem.IsMacOS() ? 0x1000000 : 0x80000;
    private static int NonBlocking => OperatingSystem.IsMacOS() ? 0x4 : 0x800;

    internal static UnixWorkspaceDirectory Open(string path)
    {
        int flags = DirectoryOnly | NoFollow | CloseOnExec;
        SafeFileHandle current = Checked(OpenAt(-1, "/", flags, 0), path);
        try
        {
            foreach (string segment in Path.GetFullPath(path).Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var next = Checked(OpenAt(current.DangerousGetHandle().ToInt32(), segment, flags, 0), path);
                current.Dispose();
                current = next;
            }
            return new(current);
        }
        catch { current.Dispose(); throw; }
    }

    private int Fd => directory.DangerousGetHandle().ToInt32();

    internal FileStream Read(string name)
    {
        var fd = Checked(OpenAt(Fd,name,NoFollow | NonBlocking | CloseOnExec,0), name);
        try { return new FileStream(fd, FileAccess.Read); }
        catch { fd.Dispose(); throw; }
    }

    internal FileStream Create(string name, UnixFileMode mode)
    {
        int create = OperatingSystem.IsMacOS() ? 0x0a00 : 0x00c0; // O_CREAT | O_EXCL.
        int flags = 1 | create | NoFollow | CloseOnExec;
        var fd = Checked(OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? OpenAtArm64(Fd,name,flags,0,0,0,0,0,(uint)mode)
            : OpenAt(Fd,name,flags,(uint)mode), name);
        try { return new FileStream(fd, FileAccess.Write); }
        catch { fd.Dispose(); throw; }
    }

    internal void Rename(string from, UnixWorkspaceDirectory target, string to, bool replace)
    {
        if (replace)
        {
            if (RenameAt(Fd,from,target.Fd,to) != 0) throw Error(to);
        }
        else
        {
            if (LinkAt(Fd,from,target.Fd,to,0) != 0) throw Error(to);
            Delete(from);
        }
    }

    internal void EnsureChild(string name)
    {
        if (MkdirAt(Fd,name,0x1c0) != 0 && Marshal.GetLastPInvokeError() != 17) throw Error(name);
        using var checkedChild = Checked(OpenAt(Fd,name,DirectoryOnly | NoFollow | CloseOnExec,0), name);
    }

    internal void Delete(string name)
    {
        if (UnlinkAt(Fd,name,0) != 0 && Marshal.GetLastPInvokeError() != 2) throw Error(name);
    }

    private static SafeFileHandle Checked(int fd, string path) => fd >= 0 ? new(fd,true) : throw Error(path);
    private static Exception Error(string path)
    {
        int error = Marshal.GetLastPInvokeError();
        return error switch
        {
            2 => new FileNotFoundException("文件或目录已不存在", path),
            17 => new WorkbenchError("目标文件已存在，请刷新后重试", WorkbenchFailure.Conflict),
            _ => new WorkbenchError("无法安全访问文件或目录：" + new Win32Exception(error).Message, WorkbenchFailure.Forbidden)
        };
    }

    public void Dispose() => directory.Dispose();

    [DllImport("libc", EntryPoint="openat", SetLastError=true)]
    private static extern int OpenAt(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mode);
    // Darwin arm64 的 C 可变参数位于栈上。占满普通调用的参数寄存器后，mode 位于第一个栈槽。
    [DllImport("libc", EntryPoint="openat", SetLastError=true)]
    private static extern int OpenAtArm64(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags,
        nint ignored3, nint ignored4, nint ignored5, nint ignored6, nint ignored7, uint mode);
    [DllImport("libc", EntryPoint="renameat", SetLastError=true)]
    private static extern int RenameAt(int source, [MarshalAs(UnmanagedType.LPUTF8Str)] string from, int target, [MarshalAs(UnmanagedType.LPUTF8Str)] string to);
    [DllImport("libc", EntryPoint="linkat", SetLastError=true)]
    private static extern int LinkAt(int source, [MarshalAs(UnmanagedType.LPUTF8Str)] string from, int target, [MarshalAs(UnmanagedType.LPUTF8Str)] string to, int flags);
    [DllImport("libc", EntryPoint="unlinkat", SetLastError=true)]
    private static extern int UnlinkAt(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint="mkdirat", SetLastError=true)]
    private static extern int MkdirAt(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
    internal static int DescriptorPath(SafeFileHandle file, byte[] result) =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? FcntlArm64(file,50,0,0,0,0,0,0,result)
            : Fcntl(file,50,result);

    [DllImport("libc", EntryPoint="fcntl", SetLastError=true)]
    private static extern int Fcntl(SafeFileHandle file, int command, [Out] byte[] result);
    [DllImport("libc", EntryPoint="fcntl", SetLastError=true)]
    private static extern int FcntlArm64(SafeFileHandle file, int command, nint ignored2, nint ignored3,
        nint ignored4, nint ignored5, nint ignored6, nint ignored7, [Out] byte[] result);
}

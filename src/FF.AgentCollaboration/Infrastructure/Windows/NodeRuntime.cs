using System.Diagnostics;
using FF.AgentCollaboration.Domain;

namespace FF.AgentCollaboration.Infrastructure.Windows;

internal static class NodeRuntime
{
    internal static string ForCodeBuddy(string npmDirectory)
    {
        // npm 的 cmd shim 固定优先使用相邻 node.exe。该版本过旧时，按实际安装位置寻找兼容运行时。
        static bool Compatible(string path)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart) >= new Version(18, 20, 8);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        }
        static string? In(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)) return null;
            string path = Path.Combine(directory, "node.exe");
            return File.Exists(path) && Compatible(path) ? path : null;
        }
        return In(npmDirectory) ?? WindowsCommand.Dependency(Compatible, "node.exe")
            ?? In(Environment.GetEnvironmentVariable("NVM_SYMLINK"))
            ?? In(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs"))
            ?? throw new WorkbenchError("腾讯 CodeBuddy 需要 Node.js 18.20.8 或更高版本。请安装兼容版本后点击重新检测。", WorkbenchFailure.Unavailable);
    }
}

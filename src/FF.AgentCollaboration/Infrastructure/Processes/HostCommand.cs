using System.ComponentModel;
using System.Diagnostics;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Windows;

namespace FF.AgentCollaboration.Infrastructure.Processes;

internal static class HostCommand
{
    internal static string? Node => Dependency("node", "node.exe");
    internal static string? Npm => Dependency("npm", "npm.cmd", "npm.exe");
    internal static string? Git => Dependency("git", "git.exe");

    private static string? Dependency(string unix, params string[] windows) =>
        OperatingSystem.IsWindows() ? WindowsCommand.Dependency(windows) : OnUnixPath(unix);

    internal static string? OnUnixPath(params string[] names)
    {
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (!Path.IsPathFullyQualified(directory)) continue;
            foreach (string name in names)
            {
                string file = Path.Combine(directory, name);
                if (IsUnixExecutable(file)) return file;
            }
        }
        return null;
    }

    internal static bool IsUnixExecutable(string path) =>
        !OperatingSystem.IsWindows() && File.Exists(path) &&
        (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;

    internal static ProcessStartInfo Resolve(string executable, IReadOnlyList<string> arguments)
    {
        if (OperatingSystem.IsWindows()) return WindowsCommand.Resolve(executable, arguments);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    // 路径已由文件浏览器验证。显式使用文件管理器，并把目录作为单个参数传递。
    internal static void OpenDirectory(string directory)
    {
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
                { Arguments = "\"" + directory + "\"" }
            : OperatingSystem.IsMacOS()
                ? Resolve("/usr/bin/open", ["-b", "com.apple.finder", directory])
                : Resolve(OnUnixPath("xdg-open") ?? throw NoFileManager(), [directory]);
        try
        {
            using var process = Process.Start(start) ?? throw NoFileManager();
        }
        catch (Win32Exception)
        {
            throw NoFileManager();
        }
    }

    private static WorkbenchError NoFileManager() =>
        new("无法打开本机文件管理器。", WorkbenchFailure.Unavailable);

    internal static async Task<string?> Version(string? path, CancellationToken ct)
    {
        if (path == null) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var start = Resolve(path, ["--version"]);
            start.RedirectStandardOutput = start.RedirectStandardError = true;
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await Task.WhenAll(output, errors, process.WaitForExitAsync(timeout.Token));
                return process.ExitCode == 0 ? (await output).Trim().Split('\n')[0].Trim() : null;
            }
            finally { if (!process.HasExited) process.Kill(true); }
        }
        catch (Exception error) when (!ct.IsCancellationRequested &&
            error is Domain.WorkbenchError or OperationCanceledException or IOException or System.ComponentModel.Win32Exception)
        { return null; }
    }
}

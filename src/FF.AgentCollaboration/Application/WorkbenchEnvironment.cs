using System.Runtime.InteropServices;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Engines;
using FF.AgentCollaboration.Infrastructure.Files;
using FF.AgentCollaboration.Infrastructure.Storage;
using FF.AgentCollaboration.Infrastructure.Windows;

namespace FF.AgentCollaboration.Application;

internal sealed record RepairRequest(string Item);
internal sealed record HealthItem(string Id, string Name, bool Ready, string Description, string? Repair = null);

internal static class WorkbenchEnvironment
{
    internal static readonly string[] Engines = ["codex", "copilot", "claude"];

    internal static async Task<object> EngineDetails(string engine, CancellationToken ct)
    {
        string? path = EngineExecutable.TryFindExecutable(engine);
        string? version = await WindowsCommand.Version(path, ct);
        return new
        {
            engine,
            installed = path != null,
            path,
            version,
            diagnostic = path != null && string.IsNullOrWhiteSpace(version) ? "版本检测失败，请检查此 CLI 和运行依赖" : null
        };
    }

    internal static async Task<HealthItem[]> Inspect(WorkbenchStore store, string data, CancellationToken ct)
    {
        var (archive, handoffs) = store.Directories;
        var native = Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native");
        bool conpty = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) &&
            File.Exists(Path.Combine(native, "OpenConsole.exe")) && File.Exists(Path.Combine(native, "conpty.dll"));
        var winget = EngineInstallation.Winget();
        var result = new List<HealthItem> { new("conpty", "Windows ConPTY", conpty, conpty ? native :
            "需要 Windows 10 1809 或更高版本及随程序分发的 OpenConsole.exe 和 conpty.dll，请重新构建或解压完整程序。") };
        foreach (var (id, label, path) in new[] {
            ("winget", "WinGet", winget), ("node", "Node.js", WindowsCommand.Dependency("node.exe")),
            ("npm", "npm", WindowsCommand.Dependency("npm.cmd", "npm.exe")), ("git", "Git", WindowsCommand.Dependency("git.exe")) })
        {
            string? version = await WindowsCommand.Version(path, ct);
            bool ready = !string.IsNullOrWhiteSpace(version);
            result.Add(new(id, label, ready, ready ? version! : "未找到可运行的 " + label,
                ready ? null : id == "winget" ? "store" : winget == null ? null : "install"));
        }
        foreach (var (id, label, path) in new[] { ("data_dir", "数据库目录", data), ("archive_root", "话题归档", archive), ("handoffs", "长消息交接", handoffs) })
        {
            try { CheckStorage(id, path); result.Add(new(id, label, true, path)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or WorkbenchError)
            { result.Add(new(id, label, false, path + " · " + error.Message, "directory")); }
        }
        return result.ToArray();
    }

    private static void CheckStorage(string item, string path)
    {
        StorageDirectories.CheckDirectory(path);
        if (item == "data_dir")
        {
            using var db = File.Open(Path.Combine(path, "workbench.sqlite"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        }
    }

    internal static async Task Repair(WorkbenchStore store, string data, string item)
    {
        var (archive, handoffs) = store.Directories;
        if (item is "data_dir" or "archive_root" or "handoffs")
        {
            string path = item switch { "data_dir" => data, "archive_root" => archive, _ => handoffs };
            TopicArchive.CheckPath(path);
            Directory.CreateDirectory(path);
            CheckStorage(item, path);
            return;
        }
        string package = item switch
        {
            "node" or "npm" => "OpenJS.NodeJS.LTS",
            "git" => "Git.Git",
            _ => throw new WorkbenchError("请选择可修复的环境项")
        };
        string manager = EngineInstallation.Winget() ?? throw new WorkbenchError("请先安装 Windows 应用安装程序", WorkbenchFailure.Unavailable);
        int code = await EngineInstallation.Run(manager, ["install", "--id", package, "--exact", "--source", "winget", "--silent",
            "--disable-interactivity", "--accept-package-agreements", "--accept-source-agreements"]);
        string? executable = item == "npm" ? WindowsCommand.Dependency("npm.cmd", "npm.exe") : WindowsCommand.Dependency(item + ".exe");
        if (string.IsNullOrWhiteSpace(await WindowsCommand.Version(executable, CancellationToken.None)))
            throw new WorkbenchError($"WinGet 返回 {code}，尚未检测到可用的 {item}，请检查安装程序。", WorkbenchFailure.Unavailable);
    }
}

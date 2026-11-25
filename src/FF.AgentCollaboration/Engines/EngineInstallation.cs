using System.Diagnostics;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Windows;

namespace FF.AgentCollaboration.Engines;

internal static class EngineInstallation
{
    // 跨浏览器的安装共用本机 WinGet；排队请求在获得锁后重新检测。
    private static readonly SemaphoreSlim InstallationLock = new(1);

    internal static string PackageId(string name) => name switch
    {
        "codex" => "OpenAI.Codex",
        "copilot" => "GitHub.Copilot",
        "claude" => "Anthropic.ClaudeCode",
        _ => throw new WorkbenchError("不支持的引擎")
    };

    internal static async Task Install(string name, CancellationToken cancellation)
    {
        await InstallationLock.WaitAsync(cancellation);
        try
        {
            if (EngineExecutable.TryFindExecutable(name) != null) return;
            if (name is "traecli" or "zcode")
                throw new WorkbenchError("请按此引擎的官方安装指南安装 Windows 交互式 CLI，再重新检测");
            if (name is "codebuddy" or "qwen")
            {
                string installer = WindowsCommand.Dependency("npm.cmd", "npm.exe") ?? throw new WorkbenchError("请先安装 Node.js 和 npm");
                if (name == "qwen")
                {
                    string? node = await WindowsCommand.Version(WindowsCommand.Dependency("node.exe"), cancellation);
                    if (!Version.TryParse(node?.Trim().TrimStart('v'), out var version) || version.Major < 22)
                        throw new WorkbenchError("npm 版 Qwen Code 需要 Node.js 22 或更高版本。请先升级 Node.js，或安装千问官方独立 CLI。");
                }
                string module = name == "qwen" ? "@qwen-code/qwen-code" : "@tencent-ai/codebuddy-code";
                int result = await Run(installer, ["install", "-g", module]);
                if (result == 0 && EngineExecutable.TryFindExecutable(name) != null) return;
                throw new WorkbenchError($"npm 安装后未检测到 {name}（退出码 {result}）", WorkbenchFailure.Unavailable);
            }
            string package = PackageId(name);
            string? winget = Winget();
            int? code = null;
            if (winget != null)
            {
                var args = new List<string> { "install", "--id", package, "--exact", "--source", "winget", "--silent",
                    "--disable-interactivity", "--accept-package-agreements", "--accept-source-agreements" };
                if (name == "codex") args.AddRange(["--scope", "user"]);
                code = await Run(winget, args);
                if (EngineExecutable.TryFindExecutable(name) != null) return;
            }
            if (name == "claude" && WindowsCommand.Dependency("npm.cmd", "npm.exe") is { } npm)
            {
                code = await Run(npm, ["install", "-g", "@anthropic-ai/claude-code"]);
                if (EngineExecutable.TryFindExecutable(name) != null) return;
            }
            throw new WorkbenchError(code == null ? "请先安装 WinGet（应用安装程序）或 Claude 所需的 npm" :
                $"安装 {package} 后未检测到程序（退出码 {code}），请检查安装输出或网络后重试。", WorkbenchFailure.Unavailable);
        }
        finally { InstallationLock.Release(); }
    }

    internal static string? Winget()
    {
        string local = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string alias = Path.Combine(local, "Microsoft", "WindowsApps", "winget.exe");
        return File.Exists(alias) ? alias : WindowsCommand.OnPath("winget.exe");
    }

    internal static async Task<int> Run(string exe, IReadOnlyList<string> args)
    {
        using var process = Process.Start(WindowsCommand.Resolve(exe, args)) ?? throw new WorkbenchError("安装程序无法启动", WorkbenchFailure.Unavailable);
        await process.WaitForExitAsync();
        return process.ExitCode;
    }
}

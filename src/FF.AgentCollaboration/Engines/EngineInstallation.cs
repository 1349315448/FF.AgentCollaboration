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
        string package = PackageId(name);
        await InstallationLock.WaitAsync(cancellation);
        try
        {
            if (EngineExecutable.TryFindExecutable(name) != null) return;
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

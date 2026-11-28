using FF.AgentCollaboration.Infrastructure.Processes;
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
            if (name is "traecli" or "zcode" or "cursor" or "opencode" or "antigravity")
                throw new WorkbenchError("请按此引擎的官方安装指南安装当前系统的交互式 CLI，再重新检测");
            if (!OperatingSystem.IsWindows() || name is "codebuddy" or "qwen" or "qoder")
            {
                string installer = HostCommand.Npm ?? throw new WorkbenchError("请先安装 Node.js 和 npm");
                await RequireNpmRuntime(name, cancellation);
                string module = EngineReleases.NpmPackage(name)!;
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
            if (name == "claude" && HostCommand.Npm is { } npm)
            {
                code = await Run(npm, ["install", "-g", "@anthropic-ai/claude-code"]);
                if (EngineExecutable.TryFindExecutable(name) != null) return;
            }
            throw new WorkbenchError(code == null ? "请先安装 WinGet（应用安装程序）或 Claude 所需的 npm" :
                $"安装 {package} 后未检测到程序（退出码 {code}），请检查安装输出或网络后重试。", WorkbenchFailure.Unavailable);
        }
        finally { InstallationLock.Release(); }
    }

    private static async Task RequireNpmRuntime(string engine, CancellationToken ct)
    {
        if (engine is not ("qwen" or "qoder")) return;
        int minimum = engine == "qoder" ? 20 : 22;
        string? node = await HostCommand.Version(HostCommand.Node, ct);
        if (!Version.TryParse(node?.Trim().TrimStart('v'), out var version) || version.Major < minimum)
            throw new WorkbenchError($"npm 版 {(engine == "qoder" ? "Qoder CLI" : "Qwen Code")} 需要 Node.js {minimum} 或更高版本。");
    }

    // 依据实际可执行文件的位置更新同一份安装，避免全局 npm 另装一份而原窗格仍用旧版。
    private static (string Program, string[] Args)? Updater(string name, string path, string? local, string latest)
    {
        if (EngineExecutable.NpmPrefix(name, path) is { } prefix)
        {
            return HostCommand.Npm is { } npm
                ? (npm, ["install", "--global", "--prefix", prefix, EngineReleases.NpmPackage(name) + "@" + latest]) : null;
        }
        if (OperatingSystem.IsWindows() && name is ("codex" or "copilot" or "claude"))
        {
            string target;
            try { target = File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
            string packageId = PackageId(name);
            if (target.Split(Path.DirectorySeparatorChar).Any(part =>
                part.StartsWith(packageId + "_Microsoft.Winget.Source_", StringComparison.OrdinalIgnoreCase)))
            {
                string? scope = WingetScope(target) ?? WingetScope(path);
                return scope != null && Winget() is { } winget ? (winget, ["upgrade", "--id", packageId, "--exact", "--source", "winget",
                    "--scope", scope, "--silent", "--disable-interactivity", "--accept-package-agreements", "--accept-source-agreements"]) : null;
            }
            if (path.Contains(@"\WinGet\Links\", StringComparison.OrdinalIgnoreCase) ||
                target.Contains(@"\WinGet\Packages\", StringComparison.OrdinalIgnoreCase)) return null;
        }
        return name switch
        {
            "traecli" => (path, ["update", "--version", latest]),
            "claude" or "codebuddy" or "copilot" => (path, ["update"]),
            "qwen" when EngineReleases.Compare(local, "0.23.0") is "current" or "ahead" => (path, ["update", "--target-version", latest]),
            _ => null
        };
    }

    internal static bool CanUpdate(string name, string path, string? local, string latest) =>
        Updater(name, path, local, latest) != null;

    private static string? WingetScope(string path)
    {
        bool Under(string variable, string subdir) => Environment.GetEnvironmentVariable(variable) is { Length: > 0 } root &&
            path.StartsWith(Path.Combine(root, subdir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (Under("LOCALAPPDATA", @"Microsoft\WinGet")) return "user";
        return Under("ProgramFiles", "WinGet") || Under("ProgramFiles(x86)", "WinGet") || Under("ProgramW6432", "WinGet") ? "machine" : null;
    }

    internal static async Task Update(string name, CancellationToken ct)
    {
        await InstallationLock.WaitAsync(ct);
        try
        {
            string path = EngineExecutable.TryFindExecutable(name) ?? throw new WorkbenchError("请先安装此引擎");
            string? current = await EngineExecutable.InstalledVersion(name, path, ct);
            string latest = await EngineReleases.Latest(name, false, ct) ?? throw new WorkbenchError("暂时无法查询官方版本，请稍后重试");
            switch (EngineReleases.Compare(current, latest))
            {
                case "current": case "ahead": return;
                case "unknown": throw new WorkbenchError("无法识别当前版本，请先重新检测");
            }
            var update = Updater(name, path, current, latest) ?? throw new WorkbenchError("请根据官方指南更新当前安装");
            if (update.Args[0] == "install") await RequireNpmRuntime(name, ct);
            int code = await Run(update.Program, update.Args, TimeSpan.FromMinutes(10));
            if (code != 0) throw new WorkbenchError($"更新程序返回退出码 {code}，请查看启动工作台的控制台输出", WorkbenchFailure.Unavailable);
            string? after = await EngineExecutable.InstalledVersion(name, EngineExecutable.TryFindExecutable(name), ct);
            if (EngineReleases.Compare(after, latest) is not ("current" or "ahead"))
                throw new WorkbenchError($"更新程序已结束，当前入口仍为 {after ?? "未知版本"}，请检查安装渠道并重新检测", WorkbenchFailure.Unavailable);
        }
        finally { InstallationLock.Release(); }
    }

    internal static string? Winget()
    {
        if (!OperatingSystem.IsWindows()) return null;
        string local = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string alias = Path.Combine(local, "Microsoft", "WindowsApps", "winget.exe");
        return File.Exists(alias) ? alias : WindowsCommand.OnPath("winget.exe");
    }

    internal static async Task<int> Run(string exe, IReadOnlyList<string> args, TimeSpan? timeout = null)
    {
        var start = HostCommand.Resolve(exe, args);
        start.RedirectStandardInput = true;
        using var process = Process.Start(start) ?? throw new WorkbenchError("安装程序无法启动", WorkbenchFailure.Unavailable);
        process.StandardInput.Close();
        using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(10));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { }
            await process.WaitForExitAsync();
            throw new WorkbenchError("安装或更新超时，请重新检测当前版本并查看控制台输出", WorkbenchFailure.Unavailable);
        }
        return process.ExitCode;
    }
}

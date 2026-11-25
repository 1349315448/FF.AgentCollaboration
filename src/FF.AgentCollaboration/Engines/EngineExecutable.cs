using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Windows;

namespace FF.AgentCollaboration.Engines;

internal static class EngineExecutable
{
    internal static string FindExecutable(string name) => TryFindExecutable(name)
        ?? throw new FileNotFoundException($"找不到 {name} 原生可执行文件");

    internal static string? TryFindExecutable(string name)
    {
        if (!EngineNames.Supports(name)) return null;
        foreach (string prefix in new[] { "FF_", "LX_" })
        {
            string? configured = Environment.GetEnvironmentVariable(prefix + name.ToUpperInvariant() + "_EXE");
            if (File.Exists(configured)) return Path.GetFullPath(configured);
        }
        if (name == "zcode")
        {
            string archName = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
            // The similarly named Electron desktop launcher does not include the native TUI.
            return WindowsCommand.Dependency(path =>
                !File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "resources", "app.asar")),
                $"zcode-windows-{archName}.exe", "zcode.cmd", "zcode.exe");
        }
        if (name is "traecli" or "codebuddy" or "qwen")
        {
            string? path = WindowsCommand.Dependency(name + ".exe", name + ".cmd");
            if (name == "traecli" && path != null && Path.GetExtension(path).Equals(".cmd", StringComparison.OrdinalIgnoreCase))
            {
                string native = Path.Combine(Path.GetDirectoryName(path)!, "traex.exe");
                if (File.Exists(native)) return native;
            }
            return path;
        }
        if (name == "claude")
        {
            string native = Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "bin", "claude.exe");
            if (File.Exists(native)) return native;
        }
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        string arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
        string target = arch == "arm64" ? "aarch64-pc-windows-msvc" : "x86_64-pc-windows-msvc";
        foreach (var directory in directories)
        {
            string binary = Path.Combine(directory, name + ".exe");
            if (File.Exists(binary)) return Path.GetFullPath(binary);
            if (name == "claude" && File.Exists(Path.Combine(directory, "claude.cmd")))
                return Path.GetFullPath(Path.Combine(directory, "claude.cmd"));
            if (name == "copilot")
            {
                binary = Path.Combine(directory, $"node_modules/@github/copilot/node_modules/@github/copilot-win32-{arch}/copilot.exe");
                if (File.Exists(binary)) return Path.GetFullPath(binary);
            }
        }
        string local = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (Path.IsPathFullyQualified(local))
        {
            if (name == "codex")
            {
                string installed = Path.Combine(local, "OpenAI", "Codex", "bin");
                if (Directory.Exists(installed))
                {
                    var latest = Directory.EnumerateDirectories(installed)
                        .Select(directory => new FileInfo(Path.Combine(directory, "codex.exe")))
                        .Where(file => file.Exists).OrderByDescending(file => file.LastWriteTimeUtc)
                        .ThenBy(file => file.FullName, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                    if (latest != null) return latest.FullName;
                }
            }
            // WinGet 安装后宿主沿用旧 PATH，直接识别用户安装目录和 portable 别名。
            string links = Path.Combine(local, "Microsoft", "WinGet", "Links", name + ".exe");
            if (File.Exists(links)) return links;
            string package = Path.Combine(local, "Microsoft", "WinGet", "Packages", EngineInstallation.PackageId(name) + "_Microsoft.Winget.Source_8wekyb3d8bbwe");
            string portable = Path.Combine(package, name + ".exe");
            if (File.Exists(portable)) return portable;
            string installedBinary = name == "copilot" ? Path.Combine(local, "GitHubCopilotCLI", "copilot.exe")
                : Path.Combine(package, $"codex-{target}.exe");
            if (name != "claude" && File.Exists(installedBinary)) return installedBinary;
        }
        if (name == "codex")
        {
            foreach (string directory in directories)
            {
                string vendor = Path.Combine(directory, $"node_modules/@openai/codex/node_modules/@openai/codex-win32-{arch}/vendor/{target}");
                foreach (string subdir in new[] { "bin", "codex" })
                {
                    string binary = Path.Combine(vendor, subdir, "codex.exe");
                    if (File.Exists(binary)) return Path.GetFullPath(binary);
                }
            }
        }
        if (name == "claude")
        {
            foreach (string directory in directories.Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm")))
            {
                string package = Path.Combine(directory, "node_modules", "@anthropic-ai", "claude-code");
                foreach (string entry in new[] { "bin/claude.exe", "cli.js" })
                    if (File.Exists(Path.Combine(package, entry))) return Path.GetFullPath(Path.Combine(package, entry));
            }
        }
        return null;
    }
}

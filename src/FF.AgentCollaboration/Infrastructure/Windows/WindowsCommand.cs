using System.Diagnostics;
using System.Text.RegularExpressions;
using FF.AgentCollaboration.Domain;
using System.Text;

namespace FF.AgentCollaboration.Infrastructure.Windows;

internal static class WindowsCommand
{
    internal static string? OnPath(params string[] names)
    {
        return Find(Environment.GetEnvironmentVariable("PATH"), names);
    }

    private static string? Find(string? paths, string[] names, Func<string, bool>? accept = null)
    {
        foreach (string dir in (paths ?? "").Split(Path.PathSeparator))
        {
            string folder = dir.Trim('"');
            if (!Path.IsPathFullyQualified(folder)) continue;
            foreach (string name in names)
            {
                string path = Path.Combine(folder, name);
                if (File.Exists(path) && (accept == null || accept(path))) return path;
            }
        }
        return null;
    }

    // 安装程序可更新用户/系统 PATH，正在运行的宿主仍持有旧副本。
    internal static string? Dependency(params string[] names) => Dependency(null, names);

    internal static string? Dependency(Func<string, bool>? accept, params string[] names) =>
        Find(Environment.GetEnvironmentVariable("PATH"), names, accept) ??
        Find(Environment.ExpandEnvironmentVariables(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? ""), names, accept) ??
        Find(Environment.ExpandEnvironmentVariables(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? ""), names, accept);

    internal static ProcessStartInfo Resolve(string executable, IReadOnlyList<string> arguments)
    {
        string directory = Path.GetDirectoryName(executable)!;
        var args = arguments.ToList();
        if (Path.GetFileName(executable).Equals("codebuddy.cmd", StringComparison.OrdinalIgnoreCase))
        {
            var entry = Path.Combine(directory, "node_modules", "@tencent-ai", "codebuddy-code", "bin", "codebuddy");
            if (File.Exists(entry))
            {
                var node = NodeRuntime.ForCodeBuddy(directory);
                var command = new ProcessStartInfo(node) { UseShellExecute = false, CreateNoWindow = true };
                args.Insert(0, entry);
                command.Arguments = string.Join(' ', args.Select(QuoteArgument));
                command.Environment["PATH"] = Path.GetDirectoryName(node) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
                return command;
            }
        }
        if (Path.GetFileName(executable).Equals("claude.cmd", StringComparison.OrdinalIgnoreCase))
        {
            string package = Path.Combine(directory, "node_modules", "@anthropic-ai", "claude-code");
            string binary = Path.Combine(package, "bin", "claude.exe"), js = Path.Combine(package, "cli.js");
            if (File.Exists(binary)) executable = binary;
            else if (File.Exists(js)) executable = js;
        }
        if (Path.GetExtension(executable).ToLowerInvariant() is ".js" or ".mjs" or ".cjs")
        {
            args.Insert(0, executable);
            string local = Path.Combine(directory, "node.exe");
            executable = File.Exists(local) ? local : Dependency("node.exe") ?? throw new WorkbenchError("此 CLI 需要安装 Node.js", WorkbenchFailure.Unavailable);
        }
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetExtension(executable).Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            // /c 与批处理的 %* 转发各解析一次。先按 Win32 argv 编码，再分别转义两层 CMD 元字符。
            static string Escape(string value) => Regex.Replace(value, @"[()%!\^""<>&| ,;*?\[\]]", "^$0");
            string payload = Escape(executable) + " " + string.Join(' ', args.Select(arg => Escape(Escape(QuoteArgument(arg)))));
            start.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            start.Arguments = "/d /s /v:off /c \"" + payload + "\"";
        }
        else start.Arguments = string.Join(' ', args.Select(QuoteArgument));
        return start;
    }

    internal static string QuoteArgument(string argument)
    {
        var quoted = new StringBuilder("\"");
        int slashes = 0;
        foreach (char character in argument)
        {
            if (character == '\\') { slashes++; continue; }
            quoted.Append('\\', character == '"' ? slashes * 2 + 1 : slashes).Append(character);
            slashes = 0;
        }
        return quoted.Append('\\', slashes * 2).Append('"').ToString();
    }

}

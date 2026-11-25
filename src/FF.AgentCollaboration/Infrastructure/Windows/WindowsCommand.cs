using System.Diagnostics;
using System.Text.RegularExpressions;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Terminal;

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
            string payload = Escape(executable) + " " + string.Join(' ', args.Select(arg => Escape(Escape(TerminalSession.QuoteArgument(arg)))));
            start.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            start.Arguments = "/d /s /v:off /c \"" + payload + "\"";
        }
        else start.Arguments = string.Join(' ', args.Select(TerminalSession.QuoteArgument));
        return start;
    }

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
                if (process.ExitCode != 0) return null;
                return (await output).Trim().Split('\n')[0].Trim();
            }
            finally { if (!process.HasExited) process.Kill(true); }
        }
        catch (Exception error) when (!ct.IsCancellationRequested && error is WorkbenchError or OperationCanceledException or IOException or System.ComponentModel.Win32Exception)
        { return null; }
    }
}

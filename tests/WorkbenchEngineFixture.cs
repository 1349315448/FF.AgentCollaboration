using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

// A native executable for the workbench black-box test. Never contacts a model.
class WorkbenchEngineFixture
{
    internal static int AppServer()
    {
        if (!Console.ReadLine().Contains("\"initialize\"")) return 1;
        Console.WriteLine("{\"id\":1,\"result\":{}}");
        if (!Console.ReadLine().Contains("\"initialized\"")) return 1;
        string request = Console.ReadLine();
        if (request.Contains("\"thread/start\""))
        {
            Console.WriteLine("{\"id\":2,\"result\":{\"thread\":{\"id\":\"" + Guid.NewGuid() + "\"}}}");
            if (!Console.ReadLine().Contains("\"thread/name/set\"")) return 1;
            Console.WriteLine("{\"id\":3,\"result\":{}}");
            return 0;
        }
        if (!request.Contains("\"account/rateLimits/read\"")) return 1;
        string limits = File.ReadAllText(Environment.GetEnvironmentVariable("LX_TEST_QUOTA"));
        Console.WriteLine("{\"id\":2,\"result\":{\"rateLimits\":" + limits + "}}");
        return 0;
    }

    static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--version") { Console.WriteLine("fixture 2.1"); return 0; }
        if (Array.IndexOf(args, "--mcp-config") >= 0)
        {
            File.WriteAllLines("claude-args.txt", args);
            string grant = Environment.GetEnvironmentVariable("LX_P0_WINDOW_GRANT");
            if (!String.IsNullOrEmpty(grant) && grant == Environment.GetEnvironmentVariable("FF_P0_WINDOW_GRANT"))
                File.WriteAllText("grant-ready", "ok");
            Thread.Sleep(120000);
            return 0;
        }
        if (args.Length > 2 && args[0] == "install")
        {
            string local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            File.AppendAllText(Path.Combine(local, "installs.txt"), string.Join(" ", args) + Environment.NewLine);
            bool npm = args[2] == "@anthropic-ai/claude-code";
            if (!npm && File.Exists(Path.Combine(local, "deny.fixture"))) return 23;
            string binary = args[2] == "GitHub.Copilot" ? Path.Combine(local, "GitHubCopilotCLI", "copilot.exe")
                : Path.Combine(local, "Microsoft", "WinGet", "Links", npm || args[2] == "Anthropic.ClaudeCode" ? "claude.exe" : "codex.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(binary));
            File.Copy(Process.GetCurrentProcess().MainModule.FileName, binary);
            return 0;
        }
        if (Array.IndexOf(args, "app-server") >= 0) return AppServer();
        if (Array.IndexOf(args, "--headless") >= 0)
        {
            string header = Console.ReadLine();
            string separator = Console.ReadLine();
            if (!header.StartsWith("Content-Length:") || separator != "") return 1;
            var request = new char[int.Parse(header.Substring(15))];
            if (Console.In.ReadBlock(request, 0, request.Length) != request.Length ||
                !new string(request).Contains("\"account.getQuota\"")) return 1;
            string snapshot = File.ReadAllText(Environment.GetEnvironmentVariable("LX_TEST_QUOTA"));
            string reply = "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"quotaSnapshots\":" + snapshot + "}}";
            Console.Write("Content-Length: " + Encoding.UTF8.GetByteCount(reply) + "\r\n\r\n" + reply);
            return 0;
        }

        if (Array.IndexOf(args, "hold-lock") >= 0)
        {
            using (File.Open("session.lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
            {
                File.WriteAllText("holder.ready", "ready");
                for (int i = 0; i < 600 && !File.Exists("finish.fixture"); i++)
                    Thread.Sleep(100);
            }
            return 0;
        }
        // The writer survives closure of the original console unless the host owns its tree.
        var child = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "hold-lock");
        child.UseShellExecute = false;
        child.CreateNoWindow = true;
        child.WorkingDirectory = Environment.CurrentDirectory;
        using (var writer = Process.Start(child))
        {
            if (File.Exists("capture-input.fixture"))
            {
                Console.InputEncoding = new UTF8Encoding(false);
                File.WriteAllText("received.fixture", Console.ReadLine());
            }
            writer.WaitForExit();
        }
        return 0;
    }
}

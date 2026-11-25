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

    internal static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "login" && args[1] == "status")
        {
            bool signedOut = File.Exists("signed-out.fixture");
            Console.WriteLine(signedOut ? "Not logged in" : "Logged in");
            return signedOut ? 1 : 0;
        }
        if (args.Length == 2 && args[0] == "auth" && args[1] == "status")
        {
            Console.WriteLine(File.Exists("signed-out.fixture") ? "{\"loggedIn\":false}" : "{\"loggedIn\":true}");
            return 0;
        }
        if (args.Length == 1 && args[0] == "login")
        {
            Console.WriteLine("Fixture login terminal");
            if (Console.ReadLine() == "login") File.Delete("signed-out.fixture");
            return 0;
        }
        string versionFile = Environment.GetEnvironmentVariable("FF_TEST_ENGINE_VERSION_FILE");
        if (args.Length == 1 && args[0] == "--version")
        {
            bool node = Path.GetFileName(Process.GetCurrentProcess().MainModule.FileName).Equals("node.exe", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine(versionFile == null ? "fixture 2.1" : File.ReadAllText(node ? versionFile + ".node" : versionFile));
            return 0;
        }
        if (versionFile != null && args.Length == 5 && args[0] == "install" && args[1] == "--global" && args[2] == "--prefix")
        {
            string prefix = Path.Combine(Path.GetDirectoryName(versionFile), "npm");
            if (Path.GetFullPath(args[3]) != prefix || !args[4].StartsWith("@qwen-code/qwen-code@")) return 19;
            File.Copy(versionFile + ".package", Path.Combine(prefix, "node_modules/@qwen-code/qwen-code/package.json"), true);
            File.WriteAllText(versionFile + ".npm-call", args[4]);
            return 0;
        }
        if (args.Length == 1 && args[0] == "update" && versionFile != null)
        {
            File.AppendAllText(versionFile + ".calls", "update\n");
            File.Copy(versionFile + ".next", versionFile, true);
            return 0;
        }
        if (versionFile != null && args.Length > 2 && args[0] == "upgrade")
        {
            File.WriteAllLines(versionFile + ".winget", args);
            File.Copy(versionFile + ".next", versionFile, true);
            return 0;
        }
        if (args.Length == 1 && args[0] == "--help")
        {
            Console.WriteLine(File.Exists("qwen-legacy.fixture") ? "--acp --session-id --resume" : "--mcp-config\n  qwen sessions list");
            return 0;
        }
        if (args.Length == 1 && args[0] == "--acp")
        {
            if (!Console.ReadLine().Contains("initialize")) return 1;
            Console.WriteLine("{\"id\":1,\"result\":{\"protocolVersion\":1}}");
            string query = Console.ReadLine();
            if (query.Contains("workspace/preflight"))
            {
                Console.WriteLine(File.Exists("qwen-legacy.fixture") ? "{\"id\":2,\"error\":{\"code\":-32601,\"message\":\"Method not found\"}}" :
                    "{\"id\":2,\"result\":{\"cells\":[{\"kind\":\"auth\",\"status\":\"ok\",\"detail\":{\"hasToken\":true}}]}}");
                return 0;
            }
            if (!query.Contains("session/list")) return 1;
            var sessions = File.Exists("qwen-sessions.json") ? File.ReadAllText("qwen-sessions.json") : "[]";
            Console.WriteLine("{\"id\":2,\"result\":{\"sessions\":" + sessions + "}}");
            return 0;
        }
        if (File.Exists("qwen-legacy.fixture"))
        {
            if (Array.IndexOf(args, "--mcp-config") >= 0 || !String.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"))) return 1;
            File.WriteAllLines("qwen-launch.txt", args);
            Thread.Sleep(120000);
            return 0;
        }
        if (args.Length > 1 && args[0] == "app-server" && args[1] == "--stdio")
        {
            string create = Console.ReadLine();
            if (create.Contains("jsonrpc") || !create.Contains("createSession")) return 1;
            Console.WriteLine("{\"id\":1,\"result\":{\"status\":\"accepted\",\"result\":{\"sessionId\":\"sess_fixture1\"}}}");
            if (!Console.ReadLine().Contains("renameSession")) return 1;
            Console.WriteLine("{\"id\":2,\"result\":{\"status\":\"accepted\"}}");
            return 0;
        }
        if (args.Length > 1 && args[0] == "--cwd")
        {
            string config = Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE"), ".zcode", "cli", "config.json");
            if (!File.Exists(config) || !File.ReadAllText(config).Contains("lx_collab")) return 1;
            File.WriteAllLines("zcode-launch.txt", args);
            Thread.Sleep(120000);
            return 0;
        }
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
            if (Console.In.ReadBlock(request, 0, request.Length) != request.Length) return 1;
            string result;
            if (new string(request).Contains("\"auth.getStatus\""))
                result = "{\"isAuthenticated\":" + (File.Exists("signed-out.fixture") ? "false" : "true") + "}";
            else if (new string(request).Contains("\"account.getQuota\""))
                result = "{\"quotaSnapshots\":" + File.ReadAllText(Environment.GetEnvironmentVariable("LX_TEST_QUOTA")) + "}";
            else return 1;
            string reply = "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":" + result + "}";
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

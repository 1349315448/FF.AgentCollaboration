using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

// Exercises the native CLI contract without a Cursor installation, credentials or model access.
class CursorEngineFixture
{
    internal static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception error) { return Failed(error.ToString()); }
    }

    private static int Run(string[] args)
    {
        if (args.Length == 1)
        {
            switch (args[0])
            {
                case "status":
                    bool signedOut = File.Exists("signed-out.fixture");
                    Console.WriteLine(signedOut ? "Not logged in" : "Login successful");
                    return signedOut ? 1 : 0;
                case "login":
                    if (Console.ReadLine() != "login") return 1;
                    File.Delete("signed-out.fixture");
                    return 0;
                case "--version":
                    Console.WriteLine("2026.11.01-fixture");
                    return 0;
                case "--help":
                    Console.WriteLine(File.Exists("old-version.fixture") ? "Usage: agent" : "--plugin-dir <path>");
                    return 0;
                case "--list-models":
                    Console.WriteLine("Available models:\n\u001b[36mauto - Auto (current, default)\u001b[0m\n\tsonnet - Sonnet\nnot a model - ignored\n");
                    return 0;
                case "create-chat":
                    File.AppendAllText("created.txt", "created\n");
                    Console.WriteLine("12345678-0123-4567-89ab-0123456789ab");
                    return 0;
            }
        }
        if (Array.IndexOf(args, "--plugin-dir") < 0 || Array.IndexOf(args, "--approve-mcps") >= 0 ||
            Array.IndexOf(args, "--yolo") >= 0)
            return Failed("unexpected arguments");
        var grant = Environment.GetEnvironmentVariable("LX_P0_WINDOW_GRANT");
        if (String.IsNullOrEmpty(grant) || grant != Environment.GetEnvironmentVariable("FF_P0_WINDOW_GRANT") ||
            Environment.GetEnvironmentVariable("FF_CURSOR_MCP_0") != "fixture-remote-header" ||
            Environment.GetEnvironmentVariable("FF_CURSOR_MCP_1") != "fixture-local-env") return Failed("missing environment overrides");
        var host = Environment.GetEnvironmentVariable("FF_TEST_CURSOR_HOST");
        var root = Environment.GetEnvironmentVariable("FF_TEST_CURSOR_ROOT");
        var command = new ProcessStartInfo(host, "workbench-mcp \"" + root + "\"");
        command.UseShellExecute = false;
        command.WorkingDirectory = Path.GetDirectoryName(host);
        command.RedirectStandardInput = command.RedirectStandardOutput = true;
        using (var bridge = Process.Start(command))
        {
            bridge.StandardInput.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"list_windows\",\"arguments\":{}}}");
            bridge.StandardInput.Close();
            string response = bridge.StandardOutput.ReadToEnd();
            if (!bridge.WaitForExit(5000) || bridge.ExitCode != 0 || !response.Contains("\"content\"") || response.Contains("\"isError\":true"))
                return Failed("collaboration authorization failed");
        }
        File.WriteAllLines("cursor-launch.txt", args);
        Thread.Sleep(120000);
        return 0;
    }

    private static int Failed(string reason)
    {
        File.WriteAllText("cursor-launch-error.txt", reason);
        return 1;
    }
}

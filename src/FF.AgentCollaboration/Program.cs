using System.Text;
using FF.AgentCollaboration.Coordination;
using FF.AgentCollaboration.Infrastructure.Storage;
using FF.AgentCollaboration.Presentation.Http;
using FF.AgentCollaboration.Presentation.McpServer;

Console.InputEncoding = Console.OutputEncoding = new UTF8Encoding(false);
if (args.Length == 0 || (args.Length < 2 && args[0] != "workbench") || args[0] is not ("run" or "init" or "recover" or "resume" or "mcp" or "workbench" or "workbench-mcp"))
{
    Console.Error.WriteLine("Usage: FF.AgentCollaboration run|init|recover|resume <run-directory> [--disconnect-on-callback] [--copilot-model ID]");
    Console.Error.WriteLine("       FF.AgentCollaboration workbench [data-directory] [--open-browser] [--fixed-data-dir] [--knowledge path] [--handoffs path] [--project path] [--port 5186] [--loopback-only | --lan-http-port 5187]");
    return 64;
}
try
{
    if (args[0] == "workbench" && (args.Length == 1 || args[1].StartsWith("--", StringComparison.Ordinal)))
    {
        await WorkbenchHost.Run(WorkbenchLocation.DefaultDirectory, args[1..]);
        return 0;
    }
    string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[1]));
    if (args[0] == "mcp") { await CoordinatorMcp.Serve(root); return 0; }
    if (args[0] == "workbench-mcp")
    {
        int? port = args is [_, _, "--notify-port", var value] && int.TryParse(value, out var n) && n is > 0 and <= 65535 ? n : null;
        await WorkbenchMcp.Serve(root, port);
        return 0;
    }
    if (args[0] == "workbench") { await WorkbenchHost.Run(root, args[2..]); return 0; }
    return await Coordinator.Run(root, args);
}
catch (Exception e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}

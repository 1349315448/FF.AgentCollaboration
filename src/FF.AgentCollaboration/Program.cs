using System.Text;
using FF.AgentCollaboration.Coordination;
using FF.AgentCollaboration.Presentation.Http;
using FF.AgentCollaboration.Presentation.McpServer;

Console.InputEncoding = Console.OutputEncoding = new UTF8Encoding(false);
if (args.Length < 2 || args[0] is not ("run" or "init" or "recover" or "resume" or "mcp" or "workbench" or "workbench-mcp"))
{
    Console.Error.WriteLine("Usage: FF.AgentCollaboration run|init|recover|resume <run-directory> [--disconnect-on-callback] [--copilot-model ID]");
    Console.Error.WriteLine("       FF.AgentCollaboration workbench <data-directory> [--open-browser] [--fixed-data-dir] [--knowledge path] [--handoffs path] [--project path] [--port 5186]");
    return 64;
}
string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[1]));
try
{
    if (args[0] == "mcp") { await CoordinatorMcp.Serve(root); return 0; }
    if (args[0] == "workbench-mcp") { await WorkbenchMcp.Serve(root); return 0; }
    if (args[0] == "workbench") { await WorkbenchHost.Run(root, args[2..]); return 0; }
    return await Coordinator.Run(root, args);
}
catch (Exception e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}

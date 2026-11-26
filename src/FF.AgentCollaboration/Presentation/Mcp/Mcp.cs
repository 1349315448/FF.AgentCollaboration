using System.Text.Json;
using System.Text.Json.Nodes;

namespace FF.AgentCollaboration.Presentation.McpServer;

internal static class Mcp
{
    internal static object Tool(string name, string description, params string[] fields) =>
        new
        {
            name,
            description,
            inputSchema = new
            {
                type = "object",
                properties = fields.ToDictionary(field => field, _ => new { type = "string" }),
                required = fields,
                additionalProperties = false
            }
        };

    internal static string WindowGrant() =>
        Environment.GetEnvironmentVariable("LX_P0_WINDOW_GRANT") ?? Environment.GetEnvironmentVariable("FF_P0_WINDOW_GRANT") ?? throw new InvalidOperationException("缺少窗口绑定");

    internal static async Task Serve(object[] tools, Func<string, JsonNode, string> call, Func<string>? instructions = null,
        Func<string?>? contextUpdate = null, Func<bool>? channelEnabled = null,
        Func<CancellationToken,Task>? channel = null, Action<bool>? responseWritten = null)
    {
        using var stop = new CancellationTokenSource();
        Task? notifications = null;
        bool channelAdvertised = false;
        try
        {
            while (await Console.In.ReadLineAsync() is { } line)
            {
                var request = JsonNode.Parse(line)!;
                var id = request["id"];
                string? method = (string?)request["method"];
                if (id == null)
                {
                    if (method == "notifications/initialized" && channelAdvertised && notifications == null && channel != null)
                        notifications = Task.Run(() => channel(stop.Token),stop.Token);
                    continue;
                }
                var p = request["params"];
                object result;
                bool succeeded = true;
                try
                {
                    switch (method)
                    {
                        case "initialize":
                            var handshake = new Dictionary<string, object>
                            {
                                ["protocolVersion"] = (string?)p?["protocolVersion"] ?? "2024-11-05",
                                ["capabilities"] = new { tools = new { } },
                                ["serverInfo"] = new { name = "lx_collab", version = "1.0" }
                            };
                            if (instructions != null) handshake["instructions"] = instructions();
                            channelAdvertised = channelEnabled?.Invoke() == true;
                            if (channelAdvertised)
                                handshake["capabilities"] = new { tools = new { }, experimental = new Dictionary<string,object> { ["claude/channel"] = new { } } };
                            result = handshake;
                            break;
                        case "ping": result = new { }; break;
                        case "tools/list": result = new { tools }; break;
                        case "tools/call":
                            string message = call((string)p!["name"]!, p["arguments"] ?? new JsonObject());
                            var content = new List<object> { new { type = "text", text = message } };
                            if (contextUpdate?.Invoke() is string update) content.Add(new {type = "text", text = update});
                            result = new { content };
                            break;
                        default:
                            Console.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code = -32601, message = "Unknown method" } }));
                            continue;
                    }
                }
                catch (Exception e) when (method == "initialize")
                {
                    Console.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code = -32603, message = e.Message } }));
                    continue;
                }
                catch (Exception e) when (method == "tools/call")
                {
                    succeeded = false;
                    result = new { isError = true, content = new[] { new { type = "text", text = e.Message } } };
                }
                bool sent = false;
                try
                {
                    Console.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }));
                    Console.Out.Flush();
                    sent = succeeded;
                }
                finally { responseWritten?.Invoke(sent); }
            }
        }
        finally
        {
            stop.Cancel();
            if (notifications != null)
                try { await notifications; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }
}

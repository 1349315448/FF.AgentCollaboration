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

    internal static async Task Serve(object[] tools, Func<string, JsonNode, string> call)
    {
        while (await Console.In.ReadLineAsync() is { } line)
        {
            var request = JsonNode.Parse(line)!;
            var id = request["id"];
            if (id == null) continue;
            string? method = (string?)request["method"];
            var p = request["params"];
            object result;
            try
            {
                switch (method)
                {
                    case "initialize":
                        result = new
                        {
                            protocolVersion = (string?)p?["protocolVersion"] ?? "2024-11-05",
                            capabilities = new { tools = new { } },
                            serverInfo = new { name = "lx_collab", version = "1.0" }
                        };
                        break;
                    case "ping": result = new { }; break;
                    case "tools/list": result = new { tools }; break;
                    case "tools/call":
                        string message = call((string)p!["name"]!, p["arguments"] ?? new JsonObject());
                        result = new { content = new[] { new { type = "text", text = message } } };
                        break;
                    default:
                        Console.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code = -32601, message = "Unknown method" } }));
                        continue;
                }
            }
            catch (Exception e) when (method == "tools/call")
            {
                result = new { isError = true, content = new[] { new { type = "text", text = e.Message } } };
            }
            Console.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }));
        }
    }
}

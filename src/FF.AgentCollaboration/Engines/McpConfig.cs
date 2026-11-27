using System.Text;
using FF.AgentCollaboration.Domain;

namespace FF.AgentCollaboration.Engines;

// Each CLI keeps its existing launch-scoped collaboration server and native approval defaults.
internal static class McpConfig
{
    internal static bool Supports(string engine,string transport) => transport switch {
        "stdio" or "http" => EngineNames.Supports(engine) && engine != "antigravity",
        "sse" => EngineNames.Supports(engine) && engine is not ("codex" or "traecli" or "antigravity"),
        _ => false
    };

    internal static Dictionary<string,object> Servers(this EngineMcp mcp,string engine,object bridge)
    {
        var servers=new Dictionary<string,object>{{"lx_collab",bridge}};
        foreach (var m in mcp.Managed)
        {
            var config=new Dictionary<string,object?>();
            if (m.Transport=="stdio")
            {
                config["command"]=m.Command;
                config["args"]=m.Args;
                config["env"]=StdioEnvironment(m);
                if (engine!="qwen") config["type"]=engine=="copilot"?"local":"stdio";
            }
            else
            {
                config[engine=="qwen" && m.Transport=="http"?"httpUrl":"url"]=m.Url;
                config["headers"]=m.Headers;
                if(engine!="qwen") config["type"]=m.Transport;
            }
            if(engine=="copilot") config["tools"]=new[]{"*"};
            if(engine=="zcode") config["enabled"]=true;
            servers.Add(m.Name,config);
        }
        return servers;
    }

    internal static void AppendCodex(List<string> args,EngineMcp mcp)
    {
        static string Map(Dictionary<string,string?> values) => "{"+string.Join(", ",values.Select(p=>Quote(p.Key)+" = "+Quote(p.Value!)))+"}";
        foreach(var m in mcp.Managed)
        {
            string settings=m.Transport=="stdio"
                ? "command = "+Quote(m.Command!)+", args = ["+string.Join(", ",m.Args.Select(Quote))+"], env = "+Map(StdioEnvironment(m))
                : "url = "+Quote(m.Url!)+", http_headers = "+Map(m.Headers);
            args.AddRange(["-c","mcp_servers."+m.Name+"={"+settings+"}"]);
        }
    }

    private static Dictionary<string,string?> StdioEnvironment(ManagedMcp m) => new(m.Env,StringComparer.OrdinalIgnoreCase)
    {
        ["FF_P0_WINDOW_GRANT"] = "", ["LX_P0_WINDOW_GRANT"] = ""
    };

    private static string Quote(string value)
    {
        var result=new StringBuilder("\"");
        foreach(char c in value)
            result.Append(c switch { '"' => "\\\"", '\\' => "\\\\", _ when char.IsControl(c) => "\\u"+((int)c).ToString("x4"), _=>c.ToString() });
        return result.Append('"').ToString();
    }
}

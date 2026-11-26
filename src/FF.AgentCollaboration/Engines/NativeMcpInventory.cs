using System.Text.Json;
using Tomlyn;
using Tomlyn.Model;

namespace FF.AgentCollaboration.Engines;

internal sealed record NativeMcp(string Engine,string? Name,string Path,string? Issue = null,bool ActiveInWindow = true);

internal static class NativeMcpInventory
{
    internal static List<NativeMcp> Scan(string engine,string? project)
    {
        var entries=new List<NativeMcp>();
        var populated=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uncertain=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var path in Paths(engine,project).Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists))
        {
            bool active=engine!="zcode" || project!=null && !Paths(engine,null).Contains(path,StringComparer.OrdinalIgnoreCase);
            string? note=null;
            if(engine=="zcode" && Path.GetFileName(path)=="mcp.json") {
                string parent=Path.GetDirectoryName(Path.GetDirectoryName(path)!)!;
                string primary=Path.GetFullPath(active?".zcode/config.json":".zcode/cli/config.json",parent);
                if(populated.Contains(primary)) continue;
                if(uncertain.Contains(primary)) note="主配置含引擎专有字段，备用配置中的同名项也予以保留";
            }
            try
            {
                using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
                if(file.Length>4*1024*1024) { entries.Add(new(engine,null,path,"配置过大，未解析",active)); continue; }
                if(path.EndsWith(".toml",StringComparison.OrdinalIgnoreCase))
                {
                    using var reader=new StreamReader(file);
                    var table=TomlSerializer.Deserialize<TomlTable>(reader.ReadToEnd());
                    if(table!=null && table.TryGetValue("mcp_servers",out var value) && value is TomlTable servers)
                        entries.AddRange(servers.Keys.Select(name=>new NativeMcp(engine,name,path)));
                    continue;
                }
                using var doc=JsonDocument.Parse(file,new JsonDocumentOptions{AllowTrailingCommas=true,CommentHandling=JsonCommentHandling.Skip});
                var root=doc.RootElement;
                if(root.ValueKind!=JsonValueKind.Object) throw new JsonException();
                void Collect(JsonElement servers)
                {
                    if(servers.ValueKind!=JsonValueKind.Object) return;
                    foreach(var prop in servers.EnumerateObject())
                    {
                        if(prop.Value.ValueKind!=JsonValueKind.Object) continue;
                        bool basic=false;
                        if(engine=="zcode")
                        {
                            var item=prop.Value;
                            string? Field(string name)=>item.TryGetProperty(name,out var f) && f.ValueKind==JsonValueKind.String ? f.GetString():null;
                            bool command=!string.IsNullOrWhiteSpace(Field("command"));
                            string? type=Field("type")??(command?"stdio":"http");
                            bool valid=type=="stdio" ? command :
                                type is "http" or "remote" or "sse" && !string.IsNullOrWhiteSpace(Field("url"));
                            if(!valid) continue;
                            // Only a known-valid stdio entry suppresses the fallback. CLI versions
                            // validate their own extended options; reserve both sets of names if uncertain.
                            basic=type=="stdio" && item.EnumerateObject().All(f=>f.Name switch {
                                "type" or "command"=>f.Value.ValueKind==JsonValueKind.String,
                                "args"=>f.Value.ValueKind==JsonValueKind.Array && f.Value.EnumerateArray().All(v=>v.ValueKind==JsonValueKind.String),
                                "env"=>f.Value.ValueKind==JsonValueKind.Object && f.Value.EnumerateObject().All(v=>v.Value.ValueKind==JsonValueKind.String),
                                _=>false
                            });
                        }
                        entries.Add(new(engine,prop.Name,path,note,active));
                        if(engine=="zcode")
                        {
                            if(basic) populated.Add(path); else uncertain.Add(path);
                        }
                    }
                }
                bool localOverridesOnly=engine=="codebuddy" && Path.GetFileName(path)==".codebuddy.json" &&
                    Paths(engine,null).Take(1).FirstOrDefault()!=path;
                if(engine=="zcode") {
                    if(Path.GetFileName(path)=="mcp.json") {
                        if(root.TryGetProperty("mcpServers",out var fallback)) Collect(fallback);
                    } else if(root.TryGetProperty("mcp",out var mcp) && mcp.ValueKind==JsonValueKind.Object && mcp.TryGetProperty("servers",out var nested)) Collect(nested);
                }
                else if(root.TryGetProperty("mcpServers",out var direct) && !localOverridesOnly) Collect(direct);
                else if(engine=="copilot") Collect(root);
                if(engine is "claude" or "codebuddy" && project!=null && root.TryGetProperty("projects",out var projects) && projects.ValueKind==JsonValueKind.Object)
                    foreach(var entry in projects.EnumerateObject())
                        if(entry.Name.Replace('\\','/').Equals(project.Replace('\\','/'),StringComparison.OrdinalIgnoreCase) &&
                            entry.Value.ValueKind==JsonValueKind.Object && entry.Value.TryGetProperty("mcpServers",out var scoped)) Collect(scoped);
            }
            catch(Exception e) when(e is JsonException or TomlException or IOException or UnauthorizedAccessException)
            { entries.Add(new(engine,null,path,"原生配置无法读取或解析，暂缓注入托管项",active)); }
        }
        return entries.Distinct().ToList();
    }

    private static IEnumerable<string> Paths(string engine,string? project)
    {
        string home=Environment.GetEnvironmentVariable("USERPROFILE")??Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string UserDir(string variable,string fallback)=>Environment.GetEnvironmentVariable(variable)??Path.Combine(home,fallback);
        string ClaudeUserConfig()
        {
            string? custom=Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            string legacy=Path.Combine(custom??Path.Combine(home,".claude"),".config.json");
            if(File.Exists(legacy)) return legacy;
            string file=string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLAUDE_CODE_CUSTOM_OAUTH_URL"))?".claude.json":".claude-custom-oauth.json";
            return Path.Combine(custom??home,file);
        }
        string QwenPath(string variable,string fallback) {
            string value=Environment.GetEnvironmentVariable(variable)??fallback;
            if(value=="~") return home;
            if(value.StartsWith("~/")||value.StartsWith(@"~\")) value=Path.Combine(home,value[2..]);
            return Path.GetFullPath(value,project??home);
        }
        if(engine=="qwen") {
            string system=QwenPath("QWEN_CODE_SYSTEM_SETTINGS_PATH",Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"qwen-code","settings.json"));
            yield return system;
            yield return QwenPath("QWEN_CODE_SYSTEM_DEFAULTS_PATH",Path.Combine(Path.GetDirectoryName(system)!,"system-defaults.json"));
        }
        string[] user=engine switch {
            "codex"=>[Path.Combine(UserDir("CODEX_HOME",".codex"),"config.toml")],
            "traecli"=>[Path.Combine(UserDir("TRAE_HOME",".trae"),"traecli.toml")],
            "claude"=>[ClaudeUserConfig()],
            "copilot"=>[Path.Combine(UserDir("COPILOT_HOME",".copilot"),"mcp-config.json")],
            "codebuddy"=>[Path.Combine(UserDir("CODEBUDDY_CONFIG_DIR",".codebuddy"),".mcp.json"),Path.Combine(UserDir("CODEBUDDY_CONFIG_DIR",".codebuddy"),"mcp.json"),Path.Combine(home,".codebuddy.json")],
            "qwen"=>[Path.Combine(QwenPath("QWEN_HOME",Path.Combine(home,".qwen")),"settings.json")],
            "qoder"=>[Path.Combine(UserDir("QODER_CONFIG_DIR",".qoder"),"settings.json")],
            "zcode"=>[Path.Combine(home,".zcode","cli","config.json"),Path.Combine(home,".agents","mcp.json")], _=>[] };
        foreach(var p in engine=="codebuddy"?user.Where(File.Exists).Take(1):user) yield return p;
        if(project==null) yield break;
        if(engine=="codebuddy") yield return Path.Combine(home,".codebuddy.json");
        string[] relative=engine switch {
            "codex"=>[".codex/config.toml"],"traecli"=>[".trae/traecli.toml"],"claude"=>[".mcp.json"],
            "copilot"=>[".mcp.json",".github/mcp.json"],"codebuddy"=>[".mcp.json","mcp.json"],
            "qwen"=>[".qwen/settings.json"],"qoder"=>[".qoder/settings.json",".qoder/settings.local.json",".mcp.json"],
            "zcode"=>["zcode.json",".zcode/config.json"],_=>[] };
        var roots=new List<string>{project};
        if(engine is "codex" or "copilot" or "zcode")
        {
            var ancestors=new List<string>();
            for(var d=new DirectoryInfo(project);d!=null;d=d.Parent)
            {
                if(d.Parent!=null && d.FullName!=project) ancestors.Add(d.FullName);
                if(Path.Exists(Path.Combine(d.FullName,".git"))) { roots.AddRange(ancestors);break; }
            }
        }
        foreach(string root in roots)
        {
            var files=relative.Select(p=>Path.GetFullPath(p,root)).Where(File.Exists);
            foreach(var p in engine=="codebuddy"?files.Take(1):files) yield return p;
        }
        if(engine=="zcode") yield return Path.Combine(project,".agents","mcp.json");
    }
    internal static HashSet<string?> Reserved(string engine,string project) =>
        Scan(engine,project).Where(n=>n.ActiveInWindow).Select(n=>n.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
}

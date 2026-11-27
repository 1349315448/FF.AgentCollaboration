using System.Security.Cryptography;
using System.Text;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Files;

namespace FF.AgentCollaboration.Engines;

internal sealed record NativeSkill(string Id,string Name,string Path,string[] Engines,bool CanDelete,bool ZCodeUserIsolated);
internal static class EngineSkillRoots
{
    private static readonly Dictionary<string,(string Variable,string Folder)> Homes=new()
    {
        ["codex"]=("CODEX_HOME",".codex"), ["copilot"]=("COPILOT_HOME",".copilot"), ["claude"]=("CLAUDE_CONFIG_DIR",".claude"),
        ["codebuddy"]=("CODEBUDDY_CONFIG_DIR",".codebuddy"), ["traecli"]=("TRAE_HOME",".trae"),
        ["qwen"]=("QWEN_HOME",".qwen"), ["qoder"]=("QODER_CONFIG_DIR",".qoder"), ["zcode"]=("",".zcode"), ["cursor"]=("",".cursor"),
        ["opencode"]=("XDG_CONFIG_HOME",".opencode"), ["antigravity"]=("",".gemini/config")
    };
    private static string Home=>Environment.GetEnvironmentVariable("USERPROFILE")??Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string Key(string path)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        OperatingSystem.IsWindows()?Path.GetFullPath(path).ToUpperInvariant():Path.GetFullPath(path)))).ToLowerInvariant();
    private static string User(string engine,string? cwd)
    {
        var (variable,folder)=Homes[engine];
        string? value=variable.Length==0?null:Environment.GetEnvironmentVariable(variable);
        if(string.IsNullOrWhiteSpace(value)) value=Path.Combine(Home,engine=="opencode"?".config":folder);
        if(value=="~") value=Home;
        else if(value.StartsWith("~/")||value.StartsWith(@"~\")) value=Path.Combine(Home,value[2..]);
        if(!Path.IsPathFullyQualified(value) && cwd==null) throw new WorkbenchError("CLI 的用户目录为相对路径，请使用绝对目录后同步");
        return engine=="opencode"?Path.Combine(Path.GetFullPath(value,cwd??Home),"opencode","skills"):
            Path.Combine(Path.GetFullPath(value,cwd??Home),"skills");
    }
    private static string Project(string engine,string project)=>Path.Combine(project,engine switch {
        "copilot"=>".github", "antigravity"=>".agents", _=>Homes[engine].Folder },"skills");
    internal static string ClaudeOverlay(string data,string project)=>Path.Combine(data,"engine-profiles","skill-roots","claude",Key(project));
    internal static string ZCodeGlobal(string data)=>Path.Combine(data,"engine-profiles","skill-roots","zcode","skills");
    internal static bool PrivateTarget(SkillTarget t)=>t.Engine=="library" || t.Engine=="claude" && t.Project.Length>0 ||
        t.Engine=="zcode" && t.Project.Length==0;
    internal static string Destination(string data,SkillTarget t) => t.Engine switch {
        "library"=>Path.Combine(data,"managed-skills"),
        "claude" when t.Project.Length>0=>Path.Combine(ClaudeOverlay(data,t.Project),".claude","skills"),
        "zcode" when t.Project.Length==0=>ZCodeGlobal(data),
        _ when t.Project.Length==0=>User(t.Engine,null),
        _=>Project(t.Engine,t.Project)
    };

    internal static (NativeSkill[] Native,List<string> Issues) Inventory(string? project)
    {
        var seen=new Dictionary<string,HashSet<string>>(OperatingSystem.IsWindows()?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal);
        var protectedSkills=new HashSet<string>(seen.Comparer);
        var isolated=new HashSet<string>(seen.Comparer);
        var issues=new List<string>();
        void Scan(string root,string engine,bool readOnly=false,bool isolatedUser=false)
        {
            if(!Directory.Exists(root)) return;
            try
            {
                using var guard=WorkspaceDirectoryGuard.Open(root);
                foreach(var dir in new DirectoryInfo(root).EnumerateDirectories())
                {
                    if(dir.Name.StartsWith('.') || dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    string file=Path.Combine(dir.FullName,"SKILL.md");
                    if(!File.Exists(file)) continue;
                    using var child=WorkspaceDirectoryGuard.Open(dir.FullName);
                    TopicArchive.CheckPath(file);
                    if(Path.Exists(Path.Combine(dir.FullName,SkillFiles.Marker))) continue;
                    if(!seen.TryGetValue(dir.FullName,out var list)) seen[dir.FullName]=list=[];
                    list.Add(engine);
                    if(readOnly) protectedSkills.Add(dir.FullName);
                    if(isolatedUser) isolated.Add(dir.FullName);
                }
            }
            catch(Exception error) when(error is IOException or UnauthorizedAccessException or WorkbenchError)
            {issues.Add(engine+" · 未能完整读取 "+root);}
        }
        foreach(var engine in EngineNames.All)
        {
            try {
                string user=User(engine,project);
                Scan(user,engine,isolatedUser:engine=="zcode");
                if(engine=="codex") Scan(Path.Combine(user,".system"),engine,readOnly:true);
            } catch(WorkbenchError e){issues.Add(engine+" · "+e.Message);}
            if(project!=null) Scan(Project(engine,project),engine);
        }
        foreach(string engine in new[]{"codex","copilot","qwen","zcode","cursor"}) Scan(Path.Combine(Home,".agents","skills"),engine,isolatedUser:engine=="zcode");
        if(project!=null)
        {
            Scan(Project("claude",project),"copilot");
            foreach(string engine in new[]{"codex","copilot","qwen","zcode","cursor"}) Scan(Path.Combine(project,".agents","skills"),engine);
            var ancestors=new List<string>();
            for(var d=new DirectoryInfo(project);d!=null;d=d.Parent)
            {
                if(!ProjectFolders.Same(d.FullName,project)) ancestors.Add(d.FullName);
                if(!Path.Exists(Path.Combine(d.FullName,".git"))) continue;
                foreach(var parent in ancestors)
                {
                    foreach(var engine in new[]{"codex","copilot","zcode"}) Scan(Project(engine,parent),engine,readOnly:true);
                    foreach(var engine in new[]{"codex","zcode"}) Scan(Path.Combine(parent,".agents","skills"),engine,readOnly:true);
                }
                break;
            }
        }
        return (seen.OrderBy(p=>p.Key,StringComparer.OrdinalIgnoreCase).Select(p=>new NativeSkill(Key(p.Key),Path.GetFileName(p.Key),p.Key,
            p.Value.Order().ToArray(),!protectedSkills.Contains(p.Key) && !Path.GetFileName(p.Key).Equals("ff-agent-collab",StringComparison.OrdinalIgnoreCase),
            isolated.Contains(p.Key))).ToArray(),issues.Distinct().ToList());
    }
}

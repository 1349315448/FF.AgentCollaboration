using System.Text.Json;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Engines;

namespace FF.AgentCollaboration.Infrastructure.Storage;

internal sealed partial class WorkbenchStore
{
    private void InitializeManagedMcps() => Exec("""
        CREATE TABLE IF NOT EXISTS managed_mcps(
            id TEXT PRIMARY KEY, name TEXT NOT NULL COLLATE NOCASE UNIQUE, transport TEXT NOT NULL,
            command TEXT, args_json TEXT NOT NULL, url TEXT, headers_json TEXT NOT NULL, env_json TEXT NOT NULL,
            scope TEXT NOT NULL, project_path TEXT, engines_json TEXT NOT NULL, enabled INTEGER NOT NULL,
            created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS mcp_restart(window_id TEXT PRIMARY KEY, revision INTEGER NOT NULL);
        """);

    internal List<ManagedMcp> ManagedMcps() => Read("""
        SELECT id,name,transport,command,args_json,url,headers_json,env_json,scope,project_path,engines_json,enabled,updated_at
        FROM managed_mcps ORDER BY name
        """,r => new ManagedMcp { Id=r.GetString(0),Name=r.GetString(1),Transport=r.GetString(2),Command=Optional(r,3),
            Args=JsonSerializer.Deserialize<string[]>(r.GetString(4))!,Url=Optional(r,5),
            Headers=JsonSerializer.Deserialize<Dictionary<string,string?>>(r.GetString(6))!,
            Env=JsonSerializer.Deserialize<Dictionary<string,string?>>(r.GetString(7))!,Scope=r.GetString(8),ProjectPath=Optional(r,9),
            Engines=JsonSerializer.Deserialize<string[]>(r.GetString(10))!,Enabled=r.GetBoolean(11),UpdatedAt=r.GetString(12) });

    internal List<ManagedMcp> SaveMcps(IReadOnlyList<ManagedMcp> inputs, string? editId = null)
    {
        if (inputs.Count is 0 or > 128) throw new WorkbenchError("每次可保存 1 到 128 个 MCP");
        var saved = new List<ManagedMcp>();
        Atomic(() => {
            var entries=ManagedMcps();
            var names=entries.Where(x=>x.Id!=editId).Select(x=>x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var prior=editId==null?null:entries.Find(x=>x.Id==editId) ?? throw new WorkbenchError("MCP 已删除",WorkbenchFailure.NotFound);
            var changes=new List<ManagedMcp>();
            foreach (var item in inputs)
            {
                if (item==null) throw new WorkbenchError("缺少 MCP 配置");
                if (prior!=null && prior.UpdatedAt!=item.UpdatedAt) throw new WorkbenchError("MCP 已变更，请重新加载后编辑",WorkbenchFailure.Conflict);
                var m=item.Validate(prior);
                if (!names.Add(m.Name)) throw new WorkbenchError("托管库中已有此名称",WorkbenchFailure.Conflict);
                string time=DateTimeOffset.UtcNow.ToString("O");
                m=m with {Id=editId??Guid.NewGuid().ToString("N"),UpdatedAt=time,
                    ProjectPath=m.Scope=="project"?RegisteredProject(m.ProjectPath!):null};
                Exec("""
                    INSERT INTO managed_mcps VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,$p9,$p10,$p11,$p12,$p12)
                    ON CONFLICT(id) DO UPDATE SET name=excluded.name,transport=excluded.transport,command=excluded.command,
                    args_json=excluded.args_json,url=excluded.url,headers_json=excluded.headers_json,env_json=excluded.env_json,
                    scope=excluded.scope,project_path=excluded.project_path,engines_json=excluded.engines_json,enabled=excluded.enabled,updated_at=excluded.updated_at
                    """,m.Id,m.Name,m.Transport,m.Command,JsonSerializer.Serialize(m.Args),m.Url,JsonSerializer.Serialize(m.Headers),
                    JsonSerializer.Serialize(m.Env),m.Scope,m.ProjectPath,JsonSerializer.Serialize(m.Engines),m.Enabled,time);
                if(prior==null || JsonSerializer.Serialize(prior with {UpdatedAt=time})!=JsonSerializer.Serialize(m)) changes.Add(m);
                saved.Add(m.Masked());
            }
            if(changes.Count>0) MarkMcpChange(prior,changes);
        });
        return saved;
    }

    internal void DeleteMcp(string id, string? revision) => Atomic(() => {
        var previous=ManagedMcps().Find(x=>x.Id==id) ?? throw new WorkbenchError("MCP 已删除",WorkbenchFailure.NotFound);
        if (previous.UpdatedAt!=revision) throw new WorkbenchError("MCP 已变更，请刷新",WorkbenchFailure.Conflict);
        Exec("DELETE FROM managed_mcps WHERE id=$p0",id);
        MarkMcpChange(previous,[]);
    });

    private void MarkMcpChange(ManagedMcp? previous, IReadOnlyList<ManagedMcp> next)
    {
        var native=new Dictionary<(string,string),HashSet<string?>>();
        foreach (var window in Windows())
        {
            bool Applies(ManagedMcp m) => m.Applies(window.Engine,window.Project) && McpConfig.Supports(window.Engine,m.Transport);
            var candidates=next.Where(Applies).ToList();
            if(previous!=null && Applies(previous)) candidates.Add(previous);
            if(candidates.Count==0 || DedicatedReader(window.Id)) continue;
            var key=(window.Engine,window.Project);
            if(!native.TryGetValue(key,out var names)) native[key]=names=NativeMcpInventory.Reserved(window.Engine,window.Project);
            if(names.Contains(null) || candidates.All(m=>names.Contains(m.Name))) continue;
            Exec("INSERT INTO mcp_restart VALUES($p0,1) ON CONFLICT(window_id) DO UPDATE SET revision=revision+1",window.Id);
        }
    }
    internal Dictionary<string,long> McpRevisions() => Read("SELECT window_id,revision FROM mcp_restart", r=>(r.GetString(0),r.GetInt64(1))).ToDictionary();
    internal void AppliedMcps(string window,long revision) => Exec("DELETE FROM mcp_restart WHERE window_id=$p0 AND revision=$p1",window,revision);
}

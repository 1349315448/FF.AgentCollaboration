import { useEffect, useState } from "react";
import { request } from "../../services/api";
import { useWorkbench } from "../../store/workbenchStore";
import { CatalogItem, ResourceCatalog } from "./ResourceCatalog";
import { engineNames as labels } from "../../services/engines";

type Fields = Record<string,string|null>;
interface Server {
  id?:string; updatedAt?:string; name:string; transport:string; command?:string; args:string[];
  url?:string; env:Fields; headers:Fields; scope:string; projectPath?:string; engines:string[]; enabled:boolean;
}
interface Native { engine:string; name?:string; path:string; issue?:string; activeInWindow:boolean }
interface Catalog { managed:Server[]; native:Native[]; compatibility:Record<string,string[]> }
const base="/managed-mcps";
const blank=():Server=>({name:"",transport:"stdio",args:[],env:{},headers:{},scope:"global",engines:[],enabled:true});
const matches=(m:Server,n:Native,project:string)=>m.name.toLowerCase()===n.name?.toLowerCase() &&
  (!m.engines.length||m.engines.includes(n.engine)) && (m.scope==="global"||m.projectPath===project);

function Secrets({label,rows,set}:{label:string;rows:[string,string|null][];set:(v:[string,string|null][])=>void}) {
  function change(i:number,key:string,value:string|null) { set(rows.map((r,j)=>j===i?[key,value]:r)); }
  return <fieldset className="mcp-fields"><legend>{label}</legend>
    {rows.map(([k,v],i)=><div key={i}><input aria-label={label+"名称"} value={k} onChange={e=>change(i,e.target.value,v)}/>
      <input type="password" autoComplete="new-password" aria-label={label+"值"} placeholder={v===null?"••••••（保留已有值）":"值"} value={v??""} onChange={e=>change(i,k,e.target.value)}/>
      <button type="button" className="text-button" aria-label={"移除"+label+"行"} onClick={()=>set(rows.filter((_,j)=>i!==j))}>×</button></div>)}
    <button type="button" className="text-button" onClick={()=>set([...rows,["",""]])}>＋ 添加{label}</button></fieldset>;
}
function fieldMap(rows:[string,string|null][]) {
  if(new Set(rows.map(([k])=>k.toLowerCase())).size!==rows.length) throw new Error("键名重复");
  return Object.fromEntries(rows);
}

export function ManagedMcps() {
  const {snapshot}=useWorkbench();
  const projects=snapshot?.projects??[];
  const [selected,setProject]=useState(snapshot?.project??"");
  const project=projects.includes(selected)?selected:"";
  const [data,setData]=useState<Catalog>(),[epoch,reload]=useState(0);
  const [section,chooseSection]=useState("platform"),[search,find]=useState(""),[draft,setDraft]=useState<Server>(),[importing,showImport]=useState(false);
  const [nativeItem,selectNative]=useState<Native>(),[bridge,showBridge]=useState(false);
  const [source,setSource]=useState(""),[args,setArgs]=useState("[]"),[env,setEnv]=useState<[string,string|null][]>([]),[headers,setHeaders]=useState<[string,string|null][]>([]);
  const [notice,tell]=useState(""),[busy,working]=useState(false);
  useEffect(()=>{
    const controller=new AbortController();
    request<Catalog>(base+(project?"?"+new URLSearchParams({project}):""),{signal:controller.signal}).then(v=>{if(!controller.signal.aborted)setData(v);})
      .catch(e=>{if(!controller.signal.aborted)tell(e.message);});
    return ()=>controller.abort();
  },[project,epoch]);
  function edit(m:Server,isImport=false) {
    setDraft({...m});showImport(isImport);setSource("");setArgs(JSON.stringify(m.args));setEnv(Object.entries(m.env));setHeaders(Object.entries(m.headers));tell("");
  }
  function update(change:Partial<Server>) {setDraft(m=>m?{...m,...change}:m);}
  async function act(task:()=>Promise<void>) {
    working(true);tell("");
    try{await task();}catch(e){tell(e instanceof Error?e.message:"操作失败");}finally{working(false);}
  }
  async function save() {
    if(!draft)return;
    if(importing) {
      let input;try{input=JSON.parse(source);}catch{throw new Error("请检查 mcpServers JSON 格式");}
      await request(base+"/import",{method:"POST",body:{mcpServers:input?.mcpServers,scope:draft.scope,
        projectPath:draft.projectPath,engines:draft.engines,enabled:draft.enabled}});
      setSource("");setDraft(undefined);tell("已导入");
    } else {
      let argv;try{argv=JSON.parse(args);}catch{throw new Error("参数应为 JSON 字符串数组");}
      const local=draft.transport==="stdio";
      const body={...draft,command:local?draft.command:null,args:local?argv:[],env:local?fieldMap(env):{},
        url:local?null:draft.url,headers:local?{}:fieldMap(headers)};
      const saved=await request<Server>(base+(draft.id?"/"+draft.id:""),{method:draft.id?"PUT":"POST",body});
      edit(saved);tell("已保存");
    }
    reload(v=>v+1);
  }
  const match=(text:string)=>text.toLowerCase().includes(search.trim().toLowerCase());
  const platform=section==="platform";
  const managed=(data?.managed??[]).filter(m=>match(m.name));
  const native=(data?.native??[]).filter(n=>n.engine===section);
  const visible=native.filter(n=>n.name && match(n.name+" "+n.path));
  const diagnostics=native.filter(n=>n.issue && !n.name).map(n=>n.path+" · "+n.issue);
  function back() {
    setDraft(undefined);selectNative(undefined);showBridge(false);showImport(false);setSource("");setArgs("[]");setEnv([]);setHeaders([]);tell("");
  }
  return <ResourceCatalog kind="MCP" source={section} onSource={id=>{back();find("");chooseSection(id);}}
    busy={busy} loading={!data && !notice} search={search} onSearch={find} projects={projects} project={project}
    onProject={path=>{back();setData(undefined);setProject(path);}} refresh={()=>reload(v=>v+1)}
    detail={bridge?"lx_collab":nativeItem?.name || (draft?(importing?"导入 MCP":draft.id?(draft.name || "编辑 MCP"):"新增 MCP"):undefined)}
    back={back} diagnostics={diagnostics} notice={notice}
    actions={platform && <><button className="button" type="button" disabled={busy} onClick={()=>edit(blank())}>新增 MCP</button>
      <button className="button" type="button" disabled={busy} onClick={()=>edit(blank(),true)}>导入 JSON</button></>}>
    {bridge?<div className="native-config"><p>工作台为每个 Agent 窗口注入协作 MCP。CLI 通过它查找 Agent、发送消息和读取交接文档。</p>
      <dl><dt>名称</dt><dd>lx_collab</dd><dt>传输</dt><dd>stdio</dd><dt>来源</dt><dd>工作台窗口</dd></dl></div>:
    nativeItem?<div className="native-config"><dl><dt>引擎</dt><dd>{labels[section]}</dd><dt>配置文件</dt><dd>{nativeItem.path}</dd>
      <dt>窗口配置</dt><dd>{nativeItem.activeInWindow?"原生配置优先":"此用户配置位于窗口隔离目录之外"}</dd></dl>
      {data?.managed.some(m=>matches(m,nativeItem,project))&&<em>同名冲突</em>}{nativeItem.issue&&<p>{nativeItem.issue}</p>}</div>:
    draft?<form onSubmit={e=>{e.preventDefault();void act(save);}}><fieldset disabled={busy}>
      {importing?<label>mcpServers JSON<textarea value={source} onChange={e=>setSource(e.target.value)} required spellCheck={false}/></label>:
      <><label>名称<input value={draft.name} onChange={e=>update({name:e.target.value})} required/></label>
      <label>传输<select value={draft.transport} onChange={e=>update({transport:e.target.value})}>{["stdio","http","sse"].map(t=><option key={t}>{t}</option>)}</select></label>
      {draft.transport==="stdio"?<><label>启动命令<input value={draft.command??""} onChange={e=>update({command:e.target.value})} required/></label>
        <label>参数（JSON 数组）<input value={args} onChange={e=>setArgs(e.target.value)}/></label><Secrets label="环境变量" rows={env} set={setEnv}/></>:
        <><label>服务地址<input type="url" required value={draft.url??""} onChange={e=>update({url:e.target.value})}/></label><Secrets label="请求头" rows={headers} set={setHeaders}/></>}</>}
      {(projects.length>0 || draft.scope==="project") && <label>作用范围<select value={draft.scope} onChange={e=>update({scope:e.target.value,projectPath:draft.projectPath??project})}><option value="global">全局</option><option value="project">指定项目</option></select></label>}
      {draft.scope==="project"&&<label>目标项目<select required value={draft.projectPath??""} onChange={e=>update({projectPath:e.target.value})}><option value="">选择项目</option>{projects.map(p=><option key={p}>{p}</option>)}</select></label>}
      <fieldset><legend>适用引擎（未勾选时适用全部）</legend><div className="mcp-engines">{Object.entries(labels).map(([id,label])=>
        <label key={id}><input type="checkbox" checked={draft.engines.includes(id)} onChange={e=>update({engines:e.target.checked?[...draft.engines,id]:draft.engines.filter(x=>x!==id)})}/>{label}</label>)}</div></fieldset>
      {!importing&&data&&Object.entries(data.compatibility).filter(([e,ts])=>(!draft.engines.length||draft.engines.includes(e))&&!ts.includes(draft.transport)).map(([e])=><small key={e}>{labels[e]} 不支持 {draft.transport}，启动时跳过</small>)}
      {!importing&&data?.native.some(n=>n.activeInWindow&&matches(draft,n,project))&&<small>与原生配置同名，对应引擎优先使用原生配置。</small>}
      <label><input type="checkbox" checked={draft.enabled} onChange={e=>update({enabled:e.target.checked})}/>启用</label>
      <div className="mcp-actions"><button className="button primary">{importing?"确认导入":"保存 MCP"}</button>
      {draft.id&&<><button type="button" className="button" onClick={()=>void act(async()=>{const r=await request<{message:string}>(base+"/"+draft.id+"/preflight?"+new URLSearchParams(project?{project}:{}),{method:"POST"});tell(r.message);})}>预检已保存配置</button>
      <button type="button" className="text-button" onClick={()=>{if(window.confirm("删除 "+draft.name+"？"))void act(async()=>{
        await request(base+"/"+draft.id+"?"+new URLSearchParams({revision:draft.updatedAt!}),{method:"DELETE"});setDraft(undefined);reload(v=>v+1);tell("已删除");});}}>删除</button></>}</div>
    </fieldset></form>:
    <div className="catalog-list">
      {platform && match("lx_collab") && <CatalogItem name="lx_collab" meta="平台原生 · stdio" onClick={()=>{back();showBridge(true);}}/>}
      {platform ? managed.map(m=><CatalogItem key={m.id} name={m.name} meta={[m.enabled?"已启用":"已停用",m.transport,m.scope==="project"?m.projectPath:"全局"].join(" · ")} onClick={()=>edit(m)}/>) :
        visible.map(n=><CatalogItem key={n.path+"|"+n.name} name={n.name!} meta={n.path} onClick={()=>{back();selectNative(n);}}/>)}
      {data && (platform?!managed.length&&!match("lx_collab"):!visible.length) && <p className="catalog-empty">{search?"没有匹配的 MCP":"未发现原生 MCP"}</p>}
    </div>}
  </ResourceCatalog>;
}

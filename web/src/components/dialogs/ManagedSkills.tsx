import {useEffect, useState, useRef} from "react";
import {request} from "../../services/api";
import {useWorkbench} from "../../store/workbenchStore";
import { ResourceCatalog, CatalogItem } from "./ResourceCatalog";
import { engineNames as labels } from "../../services/engines";
import { MarkdownBody } from "../inspector/MarkdownBody";

type Scope="global"|"project";
interface Skill {id:string;revision:string;name:string;description:string;scope:Scope;projectPath?:string;engines:string[];enabled:boolean}
interface Native {id:string;name:string;path:string;engines:string[];canDelete:boolean;zCodeUserIsolated:boolean}
interface Catalog {managed:Skill[];pendingCleanup:Skill[];nativeCleanup:{id:string;path:string;currentPath:string}[];native:Native[];issues:string[];engines:string[]}
interface Detail {skill:Skill;content:string;files:string[]}
interface NativeDetail {skill:Native;content:string;revision:string|null;issue?:string}
interface Result {skill:Skill|null;issues:string[]}
interface Upload {path:string;data:string}
const base="/managed-skills";
const template=(name="my-skill")=>"---\nname: "+name+"\ndescription: 说明何时使用此技能\n---\n\n# "+name+"\n\n";
function fresh(project:string):Detail {
  return {skill:{id:"",revision:"",name:"",description:"",scope:project?"project":"global",projectPath:project,engines:[],enabled:true},
    content:template(),files:["SKILL.md"]};
}
function encoded(file:Blob):Promise<string> {
  return new Promise((resolve,reject)=>{const reader=new FileReader();
    reader.onload=()=>resolve(String(reader.result).split(",")[1]??"");
    reader.onerror=()=>reject(new Error("无法读取所选文件")); reader.readAsDataURL(file);});
}

export function ManagedSkills() {
  const {snapshot}=useWorkbench();
  const projects=snapshot?.projects??[];
  const [selected,setProject]=useState(snapshot?.project??"");
  const project=projects.includes(selected)?selected:"";
  const [catalog,setCatalog]=useState<Catalog>(),[generation,refresh]=useState(0);
  const [source,chooseSource]=useState("platform"),[filter,search]=useState("");
  const [builtin,showBuiltin]=useState<string>();
  const [edit,setEdit]=useState<Detail>(),[native,openNative]=useState<NativeDetail>(),[upload,setUpload]=useState<Upload[]>();
  const [confirming,confirm]=useState(false),[typed,type]=useState(""),[notice,tell]=useState(""),[busy,wait]=useState(false);
  const md=useRef<HTMLInputElement>(null),folder=useRef<HTMLInputElement>(null);
  const query=project?"?"+new URLSearchParams({project}):"";
  useEffect(()=>{
    const ctrl=new AbortController();
    request<Catalog>(base+query,{signal:ctrl.signal}).then(r=>{if(!ctrl.signal.aborted)setCatalog(r);})
      .catch(e=>{if(!ctrl.signal.aborted)tell(e.message);});
    return ()=>ctrl.abort();
  },[query,generation]);
  function clear() {showBuiltin(undefined);setEdit(undefined);openNative(undefined);setUpload(undefined);confirm(false);type("");tell("");}
  async function act(task:()=>Promise<void>) {
    wait(true);tell("");
    try{await task();}catch(e){tell(e instanceof Error?e.message:"操作失败");refresh(v=>v+1);}finally{wait(false);}
  }
  function updated(r:Result,text:string) {tell([text,...r.issues].join("\n"));refresh(v=>v+1);}
  async function pick(files:File[]) {
    if(!files.length)return;
    if(files.length>256 || files.reduce((n,f)=>n+f.size,0)>8*1024*1024) throw new Error("技能包限 256 个文件、8 MiB");
    const directory=!!files[0].webkitRelativePath;
    const entries=files.map(f=>({file:f,path:directory?f.webkitRelativePath.split("/").slice(1).join("/"):"SKILL.md"}));
    const main=entries.find(x=>x.path==="SKILL.md")?.file;
    if(!main || (!directory&&!/\.md$/i.test(main.name))) throw new Error("请选择 Markdown 文件或含 SKILL.md 的目录");
    let content=new TextDecoder("utf-8",{fatal:true}).decode(await main.arrayBuffer());
    if(!content.startsWith("---")) {
      const slug=main.name.replace(/\.md$/i,"").toLowerCase().replace(/[^a-z0-9]+/g,"-").replace(/^-|-$/g,"")||"my-skill";
      content=template(slug)+"\n"+content;
    }
    const attachments=await Promise.all(entries.filter(x=>x.file!==main).map(async x=>({path:x.path,data:await encoded(x.file)})));
    clear();setEdit({...fresh(project),content,files:entries.map(x=>x.path)});setUpload(attachments);
  }
  function change(delta:Partial<Skill>) {setEdit(d=>d?{...d,skill:{...d.skill,...delta}}:d);}
  async function save() {
    if(!edit)return;
    const s=edit.skill;
    const data={scope:s.scope,projectPath:s.scope==="project"?s.projectPath:null,engines:s.engines,enabled:s.enabled,revision:s.revision||null,content:edit.content};
    const r=upload?await request<Result>(base+"/import",{method:"POST",body:{...data,content:null,files:[...upload,{path:"SKILL.md",data:await encoded(new Blob([edit.content]))}]}}):
      await request<Result>(base+(s.id?"/"+s.id:""),{method:s.id?"PUT":"POST",body:data});
    setUpload(undefined); if(r.skill)setEdit({...edit,skill:r.skill});
    updated(r,r.issues.length?"已保存，部分位置需要处理":"已保存并同步");
  }
  const matches=(s:string)=>s.toLowerCase().includes(filter.trim().toLowerCase());
  const platform=source==="platform";
  const managed=catalog?.managed.filter(s=>matches(s.name)||matches(s.description))??[];
  const entries=catalog?.native.filter(s=>s.engines.includes(source) && (matches(s.name)||matches(s.path)))??[];
  const diagnostics=(catalog?.issues??[]).filter(issue=>issue.startsWith(source+" · ")).map(issue=>issue.slice(source.length+3));
  const detail=builtin!==undefined?"ff-agent-collab":native?.skill.name || (edit?(edit.skill.name||"新建 Skill"):undefined);
  return <ResourceCatalog kind="Skills" source={source} onSource={id=>{clear();search("");chooseSource(id);}} detail={detail} back={clear}
    projects={projects} project={project} onProject={path=>{clear();setCatalog(undefined);setProject(path);}} busy={busy} loading={!catalog && !notice}
    search={filter} onSearch={search} refresh={()=>refresh(v=>v+1)} notice={notice} diagnostics={diagnostics}
    actions={platform && <div className="mcp-actions">
          <button className="button" type="button" disabled={busy} onClick={()=>{clear();setEdit(fresh(project));}}>新建 Skill</button>
          <button className="button" type="button" disabled={busy} onClick={()=>md.current?.click()}>导入 Markdown</button>
          <button className="button" type="button" disabled={busy} onClick={()=>folder.current?.click()}>导入目录</button>
          <input ref={md} hidden type="file" accept=".md" aria-label="选择 Markdown 文件" onChange={e=>{const list=Array.from(e.target.files??[]);e.target.value="";void act(()=>pick(list));}}/>
          <input ref={el=>{folder.current=el;el?.setAttribute("webkitdirectory","");}} hidden type="file" multiple aria-label="选择技能目录"
            onChange={e=>{const list=Array.from(e.target.files??[]);e.target.value="";void act(()=>pick(list));}}/>
        </div>}>
    {detail===undefined && platform && <>
      {catalog?.nativeCleanup.map(r=><div key={r.id}><small className="skill-path">{r.currentPath}</small><button type="button" title={r.path} disabled={busy} className="text-button" onClick={()=>void act(async()=>{
        await request(base+"/native-cleanup/"+r.id,{method:"POST"});refresh(v=>v+1);tell("原生 Skill 已清理");
      })}>重试已确认的删除 · {r.path.split(/[\\/]/).pop()}</button></div>)}

        {catalog?.pendingCleanup.map(s=><button key={s.id} className="text-button" disabled={busy} onClick={()=>void act(async()=>updated(
          await request<Result>(base+"/"+s.id+"/sync",{method:"POST"}),"已核对待清理目录"))}>{s.name} · 重试清理</button>)}
    </>}
    {builtin!==undefined?<article className="markdown-preview" aria-label="Skill 正文"><MarkdownBody content={builtin}/></article>:
     edit?<form onSubmit={e=>{e.preventDefault();void act(save);}}><fieldset disabled={busy}>
          <label htmlFor="managed-skill-document">SKILL.md</label><textarea id="managed-skill-document" className="skill-editor" spellCheck={false}
            value={edit.content} onChange={e=>setEdit({...edit,content:e.target.value})}/>
          {edit.files.length>1&&<details><summary>技能包文件（{edit.files.length}）</summary>{edit.files.map(f=><div key={f}>{f}</div>)}</details>}
          {(projects.length>0 || edit.skill.scope==="project") && <label>作用范围<select value={edit.skill.scope} onChange={e=>change({scope:e.target.value as Scope,projectPath:edit.skill.projectPath??project})}>
            <option value="global">全局</option><option value="project">指定项目</option></select></label>}
          {edit.skill.scope==="project"&&<label>目标项目<select required value={edit.skill.projectPath??""} onChange={e=>change({projectPath:e.target.value})}>
            <option value="">选择项目</option>{projects.map(p=><option key={p}>{p}</option>)}</select></label>}
          <fieldset><legend>同步到引擎（未勾选时同步到全部）</legend><div className="mcp-engines">{catalog?.engines.map(id=><label key={id}>
            <input type="checkbox" checked={edit.skill.engines.includes(id)} onChange={e=>change({engines:e.target.checked?[...edit.skill.engines,id]:edit.skill.engines.filter(x=>x!==id)})}/>{labels[id]}</label>)}</div></fieldset>
          <label><input type="checkbox" checked={edit.skill.enabled} onChange={e=>change({enabled:e.target.checked})}/>启用</label>
          <div className="mcp-actions"><button className="button primary">保存 Skill</button>
            {edit.skill.id&&<><button className="button" type="button" onClick={()=>void act(async()=>updated(await request<Result>(base+"/"+edit.skill.id+"/sync",{method:"POST"}),"已核对同步位置"))}>重新同步</button>
            <button className="text-button danger" type="button" onClick={()=>{if(window.confirm("删除托管 Skill "+edit.skill.name+" 及其同步副本？"))void act(async()=>{
              const r=await request<Result>(base+"/"+edit.skill.id+"?"+new URLSearchParams({revision:edit.skill.revision}),{method:"DELETE"});clear();updated(r,"已删除托管 Skill");
            });}}>删除</button></>}</div>
        </fieldset></form>:
        native?<><small className="skill-path">{native.skill.path}</small>
          <small>{native.skill.engines.map(e=>labels[e]).join(" · ")}</small>{native.issue&&<small className="skill-status">{native.issue}</small>}<article className="markdown-preview" aria-label="Skill 正文"><MarkdownBody content={native.content}/></article>
          {native.skill.zCodeUserIsolated&&<small className="skill-path">ZCode 的工作台窗口使用独立用户目录，此用户级来源仅供盘点。</small>}
          {native.skill.canDelete&&(confirming?<fieldset disabled={busy}><label>输入完整目录名以删除<input aria-label="确认原生技能名称" value={typed} onChange={e=>type(e.target.value)} autoComplete="off"/></label>
            <button type="button" className="button danger" disabled={typed!==native.skill.name} onClick={()=>void act(async()=>{
              await request(base+"/native/"+native.skill.id+query,{method:"DELETE",body:{confirmName:typed,revision:native.revision}});clear();refresh(v=>v+1);tell("已删除原生 Skill");
            })}>确认删除原生 Skill</button> <button type="button" className="text-button" onClick={()=>{confirm(false);type("");}}>取消</button></fieldset>:
            <button type="button" className="text-button danger" onClick={()=>confirm(true)}>删除原生 Skill</button>)} </>:
      <div className="catalog-list">
        {platform && matches("ff-agent-collab") && <CatalogItem name="ff-agent-collab" meta="平台原生 · 窗口协作规范" onClick={()=>void act(async()=>{
          const doc=await request<{content:string}>(base+"/platform/ff-agent-collab");showBuiltin(doc.content);
        })}/>}
        {platform ? managed.map(s=><CatalogItem key={s.id} name={s.name} meta={[s.enabled?"已启用":"已停用",s.description].filter(Boolean).join(" · ")}
          onClick={()=>void act(async()=>setEdit(await request<Detail>(base+"/"+s.id)))}/>) :
          entries.map(s=><CatalogItem key={s.id} name={s.name} meta={s.path} onClick={()=>void act(async()=>openNative(await request<NativeDetail>(base+"/native/"+s.id+query)))}/>)}
        {catalog && (platform?!managed.length&&!matches("ff-agent-collab"):!entries.length) && <p className="catalog-empty">{filter?"没有匹配的 Skill":"未发现原生 Skill"}</p>}
      </div>}
  </ResourceCatalog>;
}

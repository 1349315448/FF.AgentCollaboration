import { memo, useState, useEffect, useMemo } from "react";
import { request } from "../../services/api";
import { WorkspaceIcon } from "../layout/WorkspaceIcon";
import { workspaceUrl } from "./workspaceFiles";
import { useGitWorkspace, type GitChange } from "./gitWorkspace";
import { fileKind, GitFileMark } from "./GitFileMark";

interface Entry {name:string; path:string; directory:boolean}
interface Props {project:string; open:(path:string)=>void; active:string}
function Directory({project, path, ignored, open, active,changes,version}:Props & {path:string; ignored:boolean;changes:Map<string,GitChange>;version:number}) {
  const [entries,setEntries]=useState<Entry[]>([]), [error,setError]=useState("");
  const [expanded,setExpanded]=useState<string[]>([]);
  useEffect(() => {
    const controller = new AbortController();
    setEntries([]); setError("");
    request<{entries:Entry[]}>(workspaceUrl("tree",{project,path,showIgnored:String(ignored)}),{signal:controller.signal})
      .then(r=>{if (!controller.signal.aborted) setEntries(r.entries);})
      .catch(e=>{if (!controller.signal.aborted) setError(e.message);});
    return ()=>controller.abort();
  },[project,path,ignored,version]);
  return <ul>{error && <li role="status">{error}</li>}{entries.map(e=>{
    const change=e.directory?undefined:changes.get(e.path);
    if(change?.code==="D") return null;
    return <li key={e.path} data-git-kind={change && fileKind(change)}>
    <button type="button" title={e.path} aria-expanded={e.directory?expanded.includes(e.path):undefined}
      aria-current={!e.directory && active===e.path?"page":undefined}
      onClick={()=>e.directory?setExpanded(xs=>xs.includes(e.path)?xs.filter(x=>x!==e.path):[...xs,e.path]):open(e.path)}>
      <WorkspaceIcon name={e.directory?(expanded.includes(e.path)?"down":"right"):"docs"}/><span className="git-path">{e.name}</span>{change&&<GitFileMark change={change}/>}
    </button>
    {e.directory && expanded.includes(e.path) && <Directory {...{project,open,active,ignored,changes,version}} path={e.path}/>}
  </li>;})}</ul>;
}
export const FileTreeView=memo(function FileTreeView(props:Props & {openDiff:(c:GitChange)=>void}) {
  const git=useGitWorkspace();
  const changes=useMemo(()=>{
    const map=new Map<string,GitChange>();
    for(const c of git.status?.changes??[]) if(!map.has(c.path)||c.group!=="staged")map.set(c.path,c);
    return map;
  },[git.status]);
  const [query,setQuery]=useState(""), [ignored,setIgnored]=useState(false), [files,setFiles]=useState<string[]>([]);
  const [status,setStatus]=useState(""), [refresh,setRefresh]=useState(0);
  useEffect(()=>{
    setFiles([]); setStatus("");
    const q=query.trim();
    if (!q) return;
    const controller=new AbortController();
    const timer=setTimeout(async()=>{
      try {
        const r=await request<{files:string[];truncated:boolean}>(workspaceUrl("search",{project:props.project,q,showIgnored:String(ignored)}),{signal:controller.signal});
        if (!controller.signal.aborted) { setFiles(r.files); setStatus(r.truncated?"结果已截断，请缩小搜索范围":r.files.length?"":"没有匹配文件"); }
      } catch(e) {if (!controller.signal.aborted) setStatus((e as Error).message);}
    },160);
    return ()=>{clearTimeout(timer);controller.abort();};
  },[query,props.project,ignored,refresh,git.version]);
  const deleted=[...changes.values()].filter(c=>c.code==="D" && c.path.toLowerCase().includes(query.trim().toLowerCase()));
  return <nav className="file-tree" aria-label="项目文件">
    <div className="tree-search"><WorkspaceIcon name="search"/><input type="search" placeholder="搜索文件名" aria-label="搜索文件名" value={query} onChange={e=>setQuery(e.target.value)}/></div>
    <div className="tree-options"><label><input type="checkbox" checked={ignored} onChange={e=>setIgnored(e.target.checked)}/>显示忽略目录</label>
      <button aria-label="刷新文件树" title="刷新文件树" onClick={()=>{setRefresh(n=>n+1);git.refresh();}}><WorkspaceIcon name="refresh"/></button></div>
    <div className="tree-list">{query.trim()?<><ul>{files.map(path=>{
      const c=changes.get(path);
      return c?.code==="D"?null:<li key={path} data-git-kind={c&&fileKind(c)}><button title={path} onClick={()=>props.open(path)}><WorkspaceIcon name="docs"/><span className="git-path">{path}</span>{c&&<GitFileMark change={c}/>}</button></li>;
    })}</ul>{status && (!deleted.length||status!=="没有匹配文件") && <small role="status">{status}</small>}</>:<Directory key={refresh} {...props} changes={changes} version={git.version} path="" ignored={ignored}/>}
    {!!deleted.length && <ul aria-label="已删除文件">{deleted.map(c=><li key={c.path} data-git-kind="deleted"><button title={c.path} onClick={()=>props.openDiff(c)}>
      <WorkspaceIcon name="docs"/><span className="git-path">{c.path}</span><GitFileMark change={c}/></button></li>)}</ul>}</div>
  </nav>;
});

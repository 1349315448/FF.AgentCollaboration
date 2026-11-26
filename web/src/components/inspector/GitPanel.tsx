import { useEffect, useState } from "react";
import { request } from "../../services/api";
import { WorkspaceIcon } from "../layout/WorkspaceIcon";
import { DiffViewer } from "./DiffViewer";
import { fileKind, GitFileMark } from "./GitFileMark";
import { gitUrl, useGitWorkspace, type GitChange, type GitDiff } from "./gitWorkspace";

const groups={staged:"已暂存",unstaged:"未暂存",untracked:"未跟踪",conflict:"合并冲突"} as const;
interface Commit { id:string; author:string; date:string; subject:string }

export default function GitPanel({initial}:{initial?:GitChange}) {
  const {project,status,issue,version,refresh}=useGitWorkspace();
  const [selection,choose]=useState<GitChange|undefined>(initial);
  const [message,setMessage]=useState("");
  const [busy,setBusy]=useState("");
  const [notice,setNotice]=useState("");
  const [loaded,setDiff]=useState<{key:string;value:GitDiff}>();
  const [error,setError]=useState("");
  const [history,showHistory]=useState(false);
  const [commits,setCommits]=useState<Commit[]>([]);
  const [historyError,setHistoryError]=useState("");
  const selected=status?.changes?.find(change=>change.path===selection?.path && change.group===selection.group);
  const diffKey=selected?JSON.stringify([selected.path,selected.group,version]):"";
  const diff=loaded?.key===diffKey?loaded.value:undefined;
  useEffect(()=>{
    setDiff(undefined);setError("");
    if(!project||!selected) return;
    const controller=new AbortController();
    request<GitDiff>(gitUrl("diff",project,{path:selected.path,staged:String(selected.group==="staged")}),{signal:controller.signal})
      .then(value=>{if(!controller.signal.aborted)setDiff({key:diffKey,value});})
      .catch(e=>{if(!controller.signal.aborted)setError(e.message);});
    return ()=>controller.abort();
  },[project,selected?.path,selected?.group,diffKey]);
  useEffect(()=>{
    if(!history||!project||!status?.repository) return;
    const controller=new AbortController();
    setHistoryError("");
    request<Commit[]>(gitUrl("log",project,{limit:"15"}),{signal:controller.signal})
      .then(value=>{if(!controller.signal.aborted)setCommits(value);})
      .catch(e=>{if(!controller.signal.aborted)setHistoryError(e.message);});
    return ()=>controller.abort();
  },[project,status?.repository,history,version]);
  async function apply(action:string,file?:GitChange) {
    if(busy||!project) return;
    if(action==="discard" && (!diff || diff.truncated || !window.confirm("放弃此文件的未暂存改动？已暂存内容会保留。\n"+file?.path))) return;
    if(action==="push" && !window.confirm("将当前分支推送到 "+status?.upstream+"？")) return;
    setNotice("");setBusy(action);
    try {
      await request("/git/"+action,{method:"POST",body:{project,path:file?.path,message:action==="commit"?message:undefined,
        expectedRevision:action==="discard"?diff?.revision:undefined,expectedBranch:status?.branch,expectedUpstream:status?.upstream}});
      if(action==="commit")setMessage("");
      setNotice("Git 操作已完成");
    } catch(e) {setNotice((e as Error).message);}
    finally {setBusy("");refresh();}
  }
  if(!project || !status?.repository) return <div className="git-empty">
    <WorkspaceIcon name="changes"/><p role={issue?"alert":"status"}>{!project?"请先选择项目":issue||status?.hint||"正在检查 Git 仓库…"}</p>
    {project && <button className="text-button" onClick={refresh}>刷新</button>}
  </div>;
  const changes=status.changes??[];
  const staged=changes.some(change=>change.group==="staged"), conflicts=changes.some(change=>change.group==="conflict");
  return <div className="git-workbench">
    <section className="git-rail" aria-label="Git 变更列表">
      <div className="git-rail-head"><strong title={status.branch}>{status.branch==="(detached)"?"游离 HEAD":status.branch}</strong>
        {status.upstream&&<span title={status.upstream}>↑{status.ahead} ↓{status.behind}</span>}
        <button className="text-button" aria-label="刷新 Git" title="刷新 Git" onClick={refresh}><WorkspaceIcon name="refresh"/></button>
      </div>
      {issue&&<p role="alert">{issue}</p>}
      <div className="git-groups">
        {!changes.length&&<p>工作区干净</p>}
        {Object.entries(groups).map(([group,title])=>{
          const entries=changes.filter(change=>change.group===group);
          return entries.length>0 && <section key={group} aria-label={title}><h3>{title}<span>{entries.length}</span></h3>
            {entries.map(change=><div key={change.path} className="git-file-row" data-git-kind={fileKind(change)} data-selected={selected?.path===change.path&&selected.group===change.group}>
              <button className="git-filename" title={change.path} onClick={()=>{choose(change);showHistory(false);}}>
                <span className="git-path">{change.path}</span><GitFileMark change={change}/>
                {change.added!=null&&change.added>0&&<small className="git-add">+{change.added}</small>}
                {change.deleted!=null&&change.deleted>0&&<small className="git-delete">−{change.deleted}</small>}
              </button>
              {group!=="conflict"&&<button title={group==="staged"?"取消暂存":"暂存"} aria-label={(group==="staged"?"取消暂存 ":"暂存 ")+change.path}
                disabled={!!busy} onClick={()=>void apply(group==="staged"?"unstage":"stage",change)}>{group==="staged"?"−":"+"}</button>}
              {group==="unstaged"&&selected?.path===change.path&&selected.group===group&&
                <button title="放弃未暂存改动" aria-label={"放弃未暂存改动 "+change.path} disabled={!!busy||!diff||diff.truncated}
                  onClick={()=>void apply("discard",change)}><WorkspaceIcon name="refresh"/></button>}
            </div>)}
          </section>;
        })}
      </div>
      <div className="git-actions">
        <textarea aria-label="提交说明" placeholder="提交说明" maxLength={4096} value={message} onChange={event=>setMessage(event.target.value)}/>
        <div>
          <button className="primary" disabled={!!busy||!staged||conflicts||!message.trim()} onClick={()=>void apply("commit")}>提交</button>
          <button disabled={!!busy||!status.upstream||conflicts} onClick={()=>void apply("pull")}>拉取</button>
          <button disabled={!!busy||!status.upstream} onClick={()=>void apply("push")}>推送</button>
          <button aria-pressed={history} onClick={()=>showHistory(x=>!x)}>历史</button>
        </div>
        {(busy||notice)&&<p role="status">{busy?"Git 正在处理…":notice}</p>}
      </div>
    </section>
    {history?<section className="git-history" aria-label="最近提交">
      {historyError?<p role="alert">{historyError}</p>:!commits.length?<p className="inspector-empty">暂无提交</p>:
      <ul>{commits.map(commit=><li key={commit.id}><strong>{commit.subject}</strong>
        <span>{commit.id} · {commit.author} · {new Date(commit.date).toLocaleString()}</span></li>)}</ul>}
    </section>:selected?<DiffViewer key={selected.group+selected.path} project={project} path={selected.path} diff={diff} error={error} version={version}/>:
      <p className="inspector-empty">选择文件以查看差异</p>}
  </div>;
}

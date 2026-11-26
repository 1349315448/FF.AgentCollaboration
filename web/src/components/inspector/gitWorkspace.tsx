import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { request } from "../../services/api";
import { selectedProject, useWorkbench } from "../../store/workbenchStore";

export interface GitChange { path:string; group:"staged"|"unstaged"|"untracked"|"conflict"; code:string; added?:number; deleted?:number }
export interface GitStatus { available:boolean; repository:boolean; branch?:string; upstream?:string; ahead:number; behind:number; changes?:GitChange[]; hint?:string }
export interface GitDiff { text:string; revision:string; truncated:boolean }
interface State { project?:string; status?:GitStatus; issue?:string; version:number; refresh:()=>void }
const Workspace = createContext<State>({version:0,refresh:()=>{}});
export function gitUrl(route:string, project:string, args:Record<string,string>={}) {
  return "/git/"+route+"?"+new URLSearchParams({project,...args});
}
export function useGitWorkspace() { return useContext(Workspace); }
export function GitWorkspace({children}:{children:ReactNode}) {
  const {snapshot,topic}=useWorkbench();
  const project=topic?.project ?? selectedProject ?? snapshot?.project;
  const [result,setResult]=useState<Omit<State,"refresh">>({version:0});
  const controller=useRef<AbortController>(undefined);
  const timer=useRef<number>(undefined);
  const previous=useRef(new Map<string, string>());
  const refresh=useCallback(()=>{
    clearTimeout(timer.current);
    controller.current?.abort();
    if (!project) return;
    const next=new AbortController();
    controller.current=next;
    request<GitStatus>(gitUrl("status",project),{signal:next.signal}).then(status=>{
      if(!next.signal.aborted) setResult(r=>({project,status,version:r.version+1}));
    }).catch(error=>{
      if(!next.signal.aborted) setResult(r=>({...r,project,status:r.project===project?r.status:undefined,issue:error.message}));
    });
  },[project]);
  useEffect(()=>{
    refresh();
    return ()=>{clearTimeout(timer.current);controller.current?.abort();};
  },[refresh]);
  useEffect(()=>{
    const current=new Map<string,string>();
    let completed=false;
    for (const view of snapshot?.windows??[]) {
      if(view.agent.project!==project) continue;
      current.set(view.agent.id,view.activity);
      if(previous.current.get(view.agent.id)==="working" && view.activity==="idle") completed=true;
      // 工作 → 审批 → 待命也必须刷新。保留等待审批时的工作标记。
      if(view.activity==="waiting" && previous.current.get(view.agent.id)==="working") current.set(view.agent.id,"working");
    }
    previous.current=current;
    if(completed) {
      clearTimeout(timer.current);
      timer.current=window.setTimeout(refresh,2000);
    }
  },[snapshot,project,refresh]);
  const value=useMemo(()=>({...(result.project===project?result:{version:0}),project,refresh}),[result,project,refresh]);
  return <Workspace.Provider value={value}>{children}</Workspace.Provider>;
}

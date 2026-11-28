import {useEffect,useRef,useState} from "react";
import {request} from "../../services/api";

export interface ReleaseStatus {
  current:string;
  available?:string|null;
  notes?:string|null;
  configured:boolean;
  managed:boolean;
  progress:{phase:string;error?:string};
  last?:{id:string;status:string;version:string};
}
interface RestartTask {id:string;name:string;body:string;assigned:boolean}
interface PlatformStatus {release:ReleaseStatus;tasks:RestartTask[]}

export function PlatformUpdates({version}:{version:string}) {
  const dialog=useRef<HTMLDialogElement>(null);
  const [state,setState]=useState<PlatformStatus>();
  const [selected,select]=useState<string[]>([]);
  const [running,setRunning]=useState<string>();
  const [feedback,setFeedback]=useState("");
  const [checking,setChecking]=useState(false);
  const release=state?.release;

  useEffect(()=>{void request<PlatformStatus>("/updates").then(setState).catch(()=>{});},[]);

  useEffect(()=>{
    if(!running)return;
    const timer=window.setInterval(()=>{
      void request<PlatformStatus>("/updates/progress").then(next=>{
        setState(previous=>({release:{...next.release,available:previous?.release.available,notes:previous?.release.notes},tasks:next.tasks}));
        if(next.release.last?.id===running){
          if(next.release.last.status==="applied")window.location.reload();
          else {setFeedback("启动检查失败，已恢复原版。未投递的任务仍可选择。");setRunning(undefined);}
        }
        if(["failed","cancelled"].includes(next.release.progress.phase)){
          setFeedback(next.release.progress.error??(next.release.progress.phase==="cancelled"?"已取消安排":"更新失败"));
          setRunning(undefined);
        }
      }).catch(()=>{setFeedback("正在等待工作台重新连接…");});
    },1500);
    return ()=>window.clearInterval(timer);
  },[running]);

  async function check(){
    setChecking(true);setFeedback("");
    try {
      const next=await request<PlatformStatus>("/updates");
      setState(next);
      select(next.tasks.filter(task=>!task.assigned).map(task=>task.id));
    }catch(error){setFeedback((error as Error).message);}
    finally{setChecking(false);}
  }

  async function start(action:"apply"|"restart",mode:"idle"|"now"){
    setChecking(true);setFeedback("");
    try{
      const next=await request<{id:string}>("/updates",{method:"POST",body:{
        action,mode,version:action==="apply"?release?.available:undefined,tasks:selected
      }});
      setRunning(next.id);
    }catch(error){setFeedback((error as Error).message);}
    finally{setChecking(false);}
  }

  const busy=!!running||checking;
  const phase=release?.progress.phase;
  return <>
    <button type="button" className="text-button brand-version platform-version" aria-label={"软件版本 "+version} title={release?.available?"有可用更新":"检查更新与重启"}
      onClick={()=>{dialog.current?.showModal();void check();}}>
      {release?.available&&<svg viewBox="0 0 16 16" width="13" height="13" fill="none" stroke="currentColor" aria-label="有可用更新"><path d="M8 12V3m-4 4 4-4 4 4" strokeWidth="1.5"/></svg>}
      {version}
    </button>
    <dialog ref={dialog} closedby="any" aria-labelledby="platform-update-title" className="platform-update-dialog">
      <header className="dialog-header"><h2 id="platform-update-title">工作台更新</h2>
        <button type="button" className="icon-button" aria-label="关闭更新" onClick={()=>dialog.current?.close()}>×</button></header>
      <div className="dialog-body fed-fields">
        <p>{version}{release?.available&&<strong> → v{release.available}</strong>}</p>
        {release?.notes&&<p className="platform-update-notes">{release.notes}</p>}
        {release&&!release.managed&&<small>使用支持更新的启动器打开工作台后可应用更新或重启。</small>}
        {release&&!release.configured&&<small>请先在安装目录配置发布公钥。</small>}
        {!!state?.tasks.length&&<fieldset className="fed-fields"><legend>启动完成后恢复</legend>
          {state.tasks.map(task=><label className="fed-row" key={task.id} title={task.body}>
            <input type="checkbox" disabled={busy} checked={selected.includes(task.id)}
              onChange={e=>select(previous=>e.target.checked?[...previous,task.id]:previous.filter(id=>id!==task.id))}/>
            <span>@{task.name} {task.body}{task.assigned?"（待重新确认）":""}</span>
          </label>)}
        </fieldset>}
        <small role="status">{feedback||release?.progress.error||(running?(phase==="waiting"?"等待 Agent 空闲":
          phase==="restarting"?"正在重启工作台…":"正在下载并检查更新…"):checking?"正在检查版本…":"")}</small>
      </div>
      <footer className="dialog-footer">
        <button type="button" className="text-button" disabled={checking} onClick={()=>void check()}>重新检查</button>
        {(phase==="waiting"||phase==="downloading")&&<button type="button" className="button"
          onClick={()=>{void request("/updates/cancel",{method:"POST"}).then(()=>{
            setRunning(undefined);
            setFeedback("已取消安排");
          }).catch(error=>setFeedback(error.message));}}>取消安排</button>}
        {release?.managed&&<button type="button" className="button" disabled={busy} onClick={()=>void start("restart","idle")}>空闲后重启</button>}
        {release?.managed&&release.available&&<>
          <button type="button" className="button" disabled={busy} onClick={()=>void start("apply","idle")}>空闲后更新</button>
          <button type="button" className="button primary" disabled={busy} onClick={()=>{
            if(window.confirm("现在更新会关闭正在运行的终端。确认继续？"))void start("apply","now");
          }}>现在更新</button>
        </>}
      </footer>
    </dialog>
  </>;
}

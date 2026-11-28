import {useEffect, useRef, useState} from "react";
import {navigation, remoteOnline} from "../../services/navigation";
import {request} from "../../services/api";
import {copyText} from "../../services/clipboard";
import {useWorkbench} from "../../store/workbenchStore";
import {WorkspaceIcon} from "../layout/WorkspaceIcon";
import "./federation.css";
import {ReturnAgentSelect} from "./ReturnAgentSelect";
import {openGroupCollaboration,type GroupCollaborationView} from "../../services/permanentGroups";

type Mode = "unlimited" | "daily" | "hourly" | "total";
export interface Grant {
  id:string; windowId:string; nodeId:string; nodeName:string; ip:string; purpose:string; status:string;
  permissionMode:string; quotaMode:Mode; quotaLimit:number|null; used:number; blocked:number; paused:boolean;
  allowUpload:boolean; connected:boolean; lastReceived:number|null;
}
interface Capsule {groupName?:string;engine?:string;quota?:{mode:Mode;limit:number|null;remaining:number|null;paused:boolean;resetsAt:number|null}; id:string; topicId:string; agentName:string; nodeName:string; nodeId:string; status:string; connected:boolean; online:boolean; error?:string; contactAgentId?:string|null; deniedAgentIds?:string[]; teamMember?:boolean}
export interface PeerMail {seq:number;topicId:string;kind:string;source:string;target:string;label:string;text:string;createdAt:string;hasDocument:boolean;workState?:string|null;deliveryState?:string|null;attachmentCount?:number}
export interface ReturnContact {id:string; windowId:string; name:string; nodeName:string; online:boolean; canMention:boolean; canRemove:boolean; grantId?:string}
interface AttachmentApproval {id:string;agentId:string;agentName:string;recipient:string;files:string[];original?:string}
export interface Federation {grants:Grant[]; remotes:Capsule[]; sharedWindows:string[]; mail:PeerMail[]; contacts?:ReturnContact[];
  requestedReviews?:string[];attachmentApprovals?:AttachmentApproval[];groups?:GroupCollaborationView[]}
interface Share {enabled:boolean; permissionMode:string; dedicatedReadOnly:boolean; link:string|null; restarted:boolean; rtc?:boolean;rtcBlockReason?:string|null;attachmentAllowed?:boolean}
interface NodeInfo {nodeId:string; nodeName:string; selectedIp:string|null; activeIp:string|null; addresses:string[]; publicEndpoint:string|null; restartRequired:boolean}
type Selection = {windowId:string;share?:Share} | {remoteId:string;topicId:string} | {grantId:string} | {contactId:string} | {attachmentId:string};

const quotaLabels = {daily:"每日", hourly:"每小时", total:"总次数", unlimited:"无限制"};
const states:Record<string,string> = {applying:"正在申请",pending:"待审批",approved:"已授权",revoked:"已吊销",rejected:"已拒绝"};
const rtcBlockMessages:Record<string,string> = {
  signaling_not_configured:"未配置信令服务器",
  bridge_missing:"缺少 WebRTC 后台组件",
  restart_required:"重启工作台后生效",
  signing_key_missing:"生成链接需要签发密钥",
  settings_unreadable:"信令设置无法解密，请重新填写"
};
const open = (detail:Selection)=>window.dispatchEvent(new CustomEvent("ff-peer-dialog",{detail}));
const put = <T,>(path:string,body:unknown)=>request<T>("/peers/"+path,{method:"PUT",body});
const errorText = (error:unknown)=>error instanceof Error ? error.message : "操作失败";

export const viewApplication=(grantId:string)=>open({grantId});
export const viewReturnContact=(contactId:string)=>open({contactId});
export const openShareSettings=(windowId:string)=>open({windowId});

export function NodePreferences() {
  const [node,setNode]=useState<NodeInfo>(),[name,setName]=useState(""),[ip,setIp]=useState(""),[note,setNote]=useState("");
  const [publicEndpoint,setPublicEndpoint]=useState("");
  const [usage,setUsage]=useState<{files:number,bytes:number}>();
  useEffect(()=>{
    const dialog=document.querySelector<HTMLDialogElement>("#settings-dialog")!;
    let generation=0;
    function load() {
      const attempt=++generation;
      if (!dialog.open) return;
      setNode(undefined);setNote("");
      request<{files:number,bytes:number}>("/peers/storage").then(v=>{if(attempt===generation)setUsage(v);}).catch(e=>{if(attempt===generation)setNote(errorText(e));});
      request<NodeInfo>("/peers/node").then(value=>{
        if (attempt!==generation || !dialog.open) return;
        setNode(value);setName(value.nodeName || "");setIp(value.selectedIp || "");
        setPublicEndpoint(value.publicEndpoint || "");
      }).catch(e=>{if (attempt===generation) setNote(errorText(e));});
    }
    dialog.addEventListener("toggle",load);
    return ()=>{generation++;dialog.removeEventListener("toggle",load);};
  },[]);
  return <section className="fed-fields"><h3>联邦节点</h3>
    <label>本端名称<input disabled={!node} maxLength={60} value={name} onChange={e=>setName(e.target.value)}/></label>
    <label>监听网卡<select disabled={!node} value={ip} onChange={e=>setIp(e.target.value)}><option value="">自动选择</option>
      {node?.addresses.map(address=><option key={address}>{address}</option>)}</select></label>
    <small>{node && `节点 ${node.nodeId.slice(0,8)} · 当前监听 ${node.activeIp || "仅本机"}`}</small>
    <label>云端协作地址（可选）<input disabled={!node} value={publicEndpoint} onChange={e=>setPublicEndpoint(e.target.value)} placeholder="公网 IPv4 或域名，可带端口" spellCheck={false}/></label>
    <small>云端填写公网地址，内网请求方留空。工作台自动生成证书，支持直连或 TCP 端口转发。</small>
    <button className="button" type="button" disabled={!node || !name.trim()} onClick={async()=>{
      try {
        const saved=await put<NodeInfo>("node",{nodeName:name,selectedIp:ip,publicEndpoint:publicEndpoint.trim()});
        setNode(saved);setPublicEndpoint(saved.publicEndpoint || "");
        setNote(saved.restartRequired ? "已保存，重启工作台后启用监听网卡" : "已保存");
      }
      catch(e){setNote(errorText(e));}
    }}>保存节点设置</button><small role="status">{note}</small>
    <h3>跨端交接文档</h3><small>{usage && `${usage.files} 份 · ${(usage.bytes/1048576).toFixed(2)} / 500 MB`}</small>
    <button className="button" type="button" onClick={async()=>{
      try {setUsage(await request("/peers/storage/cleanup",{method:"POST"}));setNote("已清理超过 30 天的文档和超过 30 分钟的未绑定上传");}
      catch(e){setNote(errorText(e));}
    }}>清理过期文档</button>
  </section>;
}

export function PeerBadge({id}:{id:string}) {
  const {snapshot}=useWorkbench();
  const fed=snapshot?.federation;
  const [busy,setBusy]=useState(false),[note,setNote]=useState("");
  useEffect(()=>{
    if (!note) return;
    const timer=setTimeout(()=>setNote(""),5000);
    return ()=>clearTimeout(timer);
  },[note]);
  if (!fed) return null;
  const enabled=fed.sharedWindows.includes(id);
  const pending=fed.grants.filter(g=>g.windowId===id && g.status==="pending").length;
  const path="windows/"+id;
  async function act(action:"toggle"|"share") {
    setBusy(true);setNote("");
    try {
      const share=await request<Share>("/peers/"+path);
      if (action==="share") {
        open({windowId:id,share});
        return;
      }
      if (share.enabled!==enabled) return;
      const terminal=snapshot?.windows.find(w=>w.agent.id===id && w.agent.online)?.terminal;
      const restart=enabled && share.dedicatedReadOnly ? terminal : null;
      if (enabled && !confirmDisable(!!restart)) return;
      await put<Share>(path,{enabled:!enabled,permissionMode:"read_only",dedicatedReadOnly:false,restartTerminal:restart});
    } catch(e){setNote(errorText(e));} finally {setBusy(false);}
  }
  const windowView=snapshot?.windows.find(w=>w.agent.id===id);
  if(windowView && !windowView.agent.isLeader) return <button type="button" className="text-button" onClick={()=>openGroupCollaboration(undefined,"external",id)}>邀请此 Agent 参与合作</button>;
  return <div className="fed-controls" role="group" aria-label="外部协作">
    <button type="button" className="text-button fed-badge" data-enabled={enabled} title="管理外部协作权限与申请" onClick={()=>open({windowId:id})}>
      <WorkspaceIcon name="network"/><span>外部协作{enabled?"已开启":"未开启"}</span>{pending>0 && <small>{pending} 待审</small>}</button>
    <button type="button" className="text-button" aria-label={(enabled?"关闭":"开启")+"外部协作"} disabled={busy} onClick={()=>act("toggle")}>{enabled?"关闭":"开启"}</button>
    <button type="button" className="text-button" title={enabled?"选择协作连接方式":"请先开启外部协作"} disabled={!enabled||busy} onClick={()=>act("share")}>分享</button>
    <span className="fed-feedback" role="status">{note}</span>
  </div>;
}
function confirmDisable(restart:boolean) {
  return window.confirm("关闭外部协作将吊销此 Agent 的全部外部授权。"+(restart?"此 Agent 会重启，正在执行的任务会中断，未提交的输入会丢失。":"")+"确认关闭？");
}
function quotaText(remote:Capsule, detail=false) {
  const q=remote.quota;
  if (remote.status !== "approved" || bindingDenied(remote)) return "";
  if (!q || !Object.hasOwn(quotaLabels,q.mode)) return detail ? "对端未提供协作次数" : "";
  const prefix=remote.connected ? "" : "上次同步 · ";
  const balance=q.mode==="unlimited" ? "不限次数" : (detail ? quotaLabels[q.mode]+" · 剩余 "+q.remaining+" / "+q.limit+" 次" : "余 "+q.remaining+" 次");
  const reset=detail && q.resetsAt ? " · "+new Date(q.resetsAt*1000).toLocaleString()+" 重置" : "";
  return prefix+(q.paused?"已暂停 · ":"")+balance+reset;
}

function bindingDenied(remote:Capsule) {
  return !!remote.contactAgentId && !!remote.deniedAgentIds?.includes(remote.contactAgentId);
}

export function RemoteCapsule({remote:r,tree=false}:{remote:Capsule;tree?:boolean}) {
  const denied=bindingDenied(r), online=remoteOnline(r), status=denied?"无权限":r.status==="approved" ? (online?"在线":"离线") : states[r.status];
  const details=()=>open({remoteId:r.id,topicId:r.topicId});
  return <button type="button" data-remote-id={r.id} data-activity={online?"idle":"offline"} className={tree?"tree-remote":"remote-capsule"}
        onClick={event=>{event.stopPropagation();if(event.detail===0) details();else if(tree) void navigation.switchTopic(r.topicId);}}
        onDoubleClick={details} title={`${r.nodeName || "申请中"} · ${status} · 双击查看详情`}>
        <WorkspaceIcon name="network"/><span className="nav-dot" aria-hidden="true"/><b>@{r.agentName}</b>
        {quotaText(r) && <span className="remote-quota">{quotaText(r)}</span>}
        {denied && !tree && <span className="remote-quota">无权限</span>}
        <small>{tree?status:(r.nodeName || states[r.status])+" · "+r.nodeId.slice(0,8)}</small>
      </button>;
}

type Run = (operation:()=>Promise<void>)=>Promise<void>;
function MailApproval({grant,run}:{grant:Grant;run:Run}) {
  const [canWrite,setCanWrite]=useState(false);
  useEffect(()=>{
    let current=true;
    request<Share>("/peers/windows/"+grant.windowId)
      .then(share=>{ if(current) setCanWrite(share.permissionMode==="read_write"); })
      .catch(()=>{ if(current) setCanWrite(false); });
    return ()=>{current=false;};
  },[grant.windowId]);
  return <GrantPolicy grant={grant} canWrite={canWrite} run={run}/>;
}
export function MailApprovals({grants}:{grants:Grant[]}) {
  return <div className="fed-mail-approvals">{grants.map(g=><div key={g.id} className="fed-application-row">
    <WorkspaceIcon name="mailRequest"/><b>{g.nodeName}</b><span title={g.purpose}>{g.purpose}</span>
    <button type="button" className="text-button" onClick={()=>viewApplication(g.id)}>查看</button>
  </div>)}{grants.length===0 && <small>没有待审申请</small>}</div>;
}
function GrantPolicy({grant,canWrite,run}:{grant:Grant;canWrite:boolean;run:Run}) {
  const [mode,setMode]=useState<Mode>(grant.quotaMode),[limit,setLimit]=useState(grant.quotaLimit??20);
  const [write,setWrite]=useState(grant.permissionMode==="read_write"),[paused,pause]=useState(grant.paused),[upload,setUpload]=useState(grant.allowUpload);
  const active=grant.status==="approved",pending=grant.status==="pending";
  async function decide(action:string,resetTotal=false) {
    const change=action==="approve" || action==="configure";
    if (change && mode!=="unlimited" && (!Number.isSafeInteger(limit)||limit<1)) throw new Error("请填写正整数配额");
    if (change && write && !window.confirm("允许此协作者请求修改项目文件？")) return;
    await put("grants/"+grant.id,{action,quotaMode:mode,quotaLimit:limit,permissionMode:write?"read_write":"read_only",confirmWrite:change&&write,paused,allowUpload:upload,resetTotal});
  }
  return <article className="fed-grant">
    <header><b>{grant.nodeName}</b><code>{grant.nodeId.slice(0,8)}</code><small>{states[grant.status]}</small></header>
    <p>{grant.purpose}</p><small>{grant.ip}{grant.lastReceived ? " · 最近来信 "+new Date(grant.lastReceived*1000).toLocaleString() : ""}</small>
    {(active||pending) && <><div className="fed-policy">
      <label>权限<select value={write?"rw":"ro"} onChange={e=>setWrite(e.target.value==="rw")}><option value="ro">只读审阅</option><option value="rw" disabled={!canWrite}>允许修改</option></select></label>
      <label>配额<select value={mode} onChange={e=>setMode(e.target.value as Mode)}>{Object.entries(quotaLabels).map(([key,value])=><option key={key} value={key}>{value}</option>)}</select></label>
      {mode!=="unlimited" && <label>次数<input type="number" min={1} value={limit||""} onChange={e=>setLimit(e.target.valueAsNumber||0)}/></label>}
    </div><div className="fed-row"><label><input type="checkbox" checked={paused} onChange={e=>pause(e.target.checked)}/>暂停接收</label>
      <label><input type="checkbox" checked={upload} onChange={e=>setUpload(e.target.checked)}/>允许文档上传</label></div>
    <small>{grant.quotaMode==="unlimited"?"无限制":`已用 ${grant.used} / ${grant.quotaLimit}`}{grant.blocked>0 && ` · 已拦截 ${grant.blocked} 次`}</small>
    <div className="fed-row"><button className="button primary" onClick={()=>run(()=>decide(active?"configure":"approve"))}>{active?"保存权限":"同意"}</button>
      {active && mode==="total" && <button className="button" onClick={()=>run(()=>decide("configure",true))}>清零计数</button>}
      <button className="text-button" onClick={()=>run(()=>decide(active?"revoke":"reject"))}>{active?"吊销":"拒绝"}</button></div></>}
  </article>;
}

function Provider({id,run}:{id:string;run:Run}) {
  const {snapshot}=useWorkbench();
  const [share,setShare]=useState<Share>();
  const [enabled,setEnabled]=useState(false),[mode,setMode]=useState("read_only"),[notice,setNotice]=useState("");
  const path="windows/"+id;
  function apply(data:Share) {
    setShare(data);setEnabled(data.enabled);setMode(data.dedicatedReadOnly?"restricted":data.permissionMode);
  }
  useEffect(()=>{let current=true;void run(async()=>{const data=await request<Share>("/peers/"+path);if(current)apply(data);});return ()=>{current=false;};},[path]);
  const grants=snapshot?.federation?.grants.filter(g=>g.windowId===id)??[];
  if (!share) return <p>正在读取共享设置…</p>;
  const terminal=snapshot?.windows.find(w=>w.agent.id===id && w.agent.online)?.terminal;
  const permission=mode==="read_write"?"read_write":"read_only";
  const restricted=enabled && mode==="restricted";
  const changesTerminal=share.dedicatedReadOnly!==restricted;
  const restart=changesTerminal && !!terminal;
  const dirty=share.enabled!==enabled || changesTerminal || (enabled && permission!==share.permissionMode);
  async function save() {
    if (!enabled && share?.enabled && !confirmDisable(restart)) return;
    if (enabled && permission==="read_write" && !window.confirm("允许为此 Agent 的协作者单独授予修改权限？")) return;
    setNotice(restart?"正在重启此 Agent…":"");
    try {
      const saved=await put<Share>(path,{enabled,permissionMode:permission,confirmWrite:permission==="read_write",
        dedicatedReadOnly:restricted,restartTerminal:restart?terminal:null});
      apply(saved);
      setNotice(saved.restarted?"已保存，终端已重启。":"已保存。");
    } catch(error) {
      setNotice("");
      // 若原生进程启动失败，配置可能已提交。按服务端实际状态显示，并保留错误原因。
      if (restart) {
        const saved=await request<Share>("/peers/"+path).catch(()=>null);
        if (saved?.dedicatedReadOnly===restricted) apply(saved);
      }
      throw error;
    }
  }
  return <><label className="fed-row"><input type="checkbox" checked={enabled} onChange={e=>{setEnabled(e.target.checked);setNotice("");}}/>开启外部协作</label>
    {enabled && <><label>协作模式<select value={mode} onChange={e=>{setMode(e.target.value);setNotice("");}}>
      <option value="read_only">外部只读 · 本机权限不变</option>
      <option value="restricted">外部只读 · 本机终端也受限</option>
      <option value="read_write">可按协作者授权修改</option></select></label>
      <small>{mode==="restricted"?"本机操作也使用引擎的只读沙箱、工具限制或计划模式，仅可向当前外部来信回信。":
        mode==="read_write"?"在下方为每位协作者单独授权后，对方才能请求修改。":"外部来信附带只读要求。在 Git 仓库中，工作台会核对文件变更。本机操作沿用原有权限。"}</small></>}
    {changesTerminal && <small>{!enabled && "保存后恢复本机终端原有权限。"}{restart?"将重启此 Agent 并恢复当前会话。正在执行的任务会中断，未提交的终端输入会丢失。":"此 Agent 已离线，新模式在下次打开终端时生效。"}</small>}
    <div className="fed-row"><button className="button primary" disabled={!dirty} onClick={()=>run(save)}>{restart?"保存并重启此 Agent":"保存设置"}</button>
      <small role="status">{notice}</small></div>
    {share.enabled && <div className="fed-row"><button className="button" disabled={!share.link} onClick={()=>run(async()=>{await copyText(share.link!);setNotice("已复制分享链接");})}>复制分享链接</button>
      <button className="text-button" disabled={dirty} onClick={()=>run(async()=>{apply(await put<Share>(path,{enabled:true,permissionMode:share.permissionMode,dedicatedReadOnly:share.dedicatedReadOnly,rotate:true,confirmWrite:share.permissionMode==="read_write"}));setNotice("分享链接已重置。");})}>重置链接</button></div>}
    {snapshot?.federation?.attachmentApprovals?.filter(item=>item.agentId===id).map(item=>
      <button className="button" key={item.id} onClick={()=>open({attachmentId:item.id})}>审核发给 {item.recipient} 的附件</button>)}
    {share.attachmentAllowed && <button className="button" onClick={()=>run(async()=>{
      await request("/peers/"+path+"/attachment-permission",{method:"DELETE"});
      setShare({...share,attachmentAllowed:false});
    })}>撤销此 Agent 的长期附件授权</button>}
    {grants.map(g=><GrantPolicy key={g.id+g.status+g.permissionMode} grant={g} canWrite={share.permissionMode==="read_write"} run={run}/>)}</>;
}

export function FederationDialog() {
  const ref=useRef<HTMLDialogElement>(null);
  const [selection,select]=useState<Selection>(),[note,notify]=useState(""),[busy,setBusy]=useState(false);
  const {snapshot}=useWorkbench();
  const shown=useRef(new Set<string>());
  useEffect(()=>{
    const show=(e:Event)=>{select((e as CustomEvent<Selection>).detail);notify("");ref.current?.showModal();};
    window.addEventListener("ff-peer-dialog",show);return ()=>window.removeEventListener("ff-peer-dialog",show);
  },[]);
  useEffect(()=>{
    const requests=snapshot?.federation?.requestedReviews ?? [];
    const attachments=snapshot?.federation?.attachmentApprovals ?? [];
    function showPending() {
      if (document.querySelector("dialog[open]")) return;
      const id=requests.find(candidate=>!shown.current.has(candidate));
      if (id) {
        shown.current.add(id);
        viewApplication(id);
        void request("/peers/grants/"+id+"/review-shown", {method:"POST"}).catch(()=>shown.current.delete(id));
        return;
      }
      const pending=attachments.find(item=>!shown.current.has(item.id));
      if (pending) {shown.current.add(pending.id);open({attachmentId:pending.id});}
    }
    showPending();
    document.addEventListener("close",showPending,true);
    return ()=>document.removeEventListener("close",showPending,true);
  },[snapshot?.federation?.requestedReviews,snapshot?.federation?.attachmentApprovals]);
  const run:Run=async op=>{setBusy(true);notify("");try{await op();}catch(e){notify(errorText(e));}finally{setBusy(false);}};
  const remote=selection && "remoteId" in selection && snapshot?.federation?.remotes.find(r=>r.id===selection.remoteId && r.topicId===selection.topicId);
  const share=selection && "windowId" in selection ? selection.share : undefined;
  const directLink=share?.link;
  const rtcHint=share && !share.rtc ? rtcBlockMessages[share.rtcBlockReason ?? ""] ?? "跨内网连接暂不可用" : undefined;
  const grantId=selection && "grantId" in selection ? selection.grantId : undefined;
  const grant=snapshot?.federation?.grants.find(g=>g.id===grantId && g.status==="pending");
  const attachment=selection && "attachmentId" in selection
    ? snapshot?.federation?.attachmentApprovals?.find(item=>item.id===selection.attachmentId) : undefined;
  const contact=selection && "contactId" in selection ? snapshot?.federation?.contacts?.find(c=>c.id===selection.contactId) : undefined;
  const owner=contact && snapshot?.windows.find(w=>w.agent.id===contact.windowId)?.agent.name;
  useEffect(()=>{if(grantId && !grant) ref.current?.close();},[grantId,grant]);
  const title=attachment?"发送附件授权":grantId ? "外部协作申请" : selection && "windowId" in selection ? "@"+(snapshot?.windows.find(w=>w.agent.id===selection.windowId)?.agent.name||"Agent")+(share?" · 分享外部协作":" · 外部协作") :
    contact?"@"+contact.name+" · 协作好友":remote?"@"+remote.agentName:"外部协作";
  return <dialog ref={ref} id="federation-dialog" closedby="any" aria-labelledby="fed-title" onClose={()=>select(undefined)}>
    <header className="dialog-header"><h2 id="fed-title">{title}</h2><button type="button" className="icon-button" aria-label="关闭" onClick={()=>ref.current?.close()}>×</button></header>
    <fieldset disabled={busy} className="dialog-body fed-fields">
    {grant && <MailApproval key={grant.id} grant={grant} run={run}/>}
    {attachment && <><p>@{attachment.agentName} → @{attachment.recipient}</p>
      {attachment.original && <small>补充到消息 {attachment.original.slice(0,8)}</small>}
      <ul>{attachment.files.map((file,i)=><li key={i}>{file}</li>)}</ul>
      <div className="fed-row">{([["once","单次授权"],["always","永久授权此 Agent"],["reject","拒绝"]] as const).map(([action,label])=>
        <button type="button" className={action==="once"?"button primary":"button"} key={action} onClick={()=>run(async()=>{
          await request("/peers/attachment-approvals/"+attachment.id,{method:"POST",body:{action}});
          ref.current?.close();
        })}>{label}</button>)}</div></>}
    {selection && "windowId" in selection && (share
      ? <div className="fed-share-options">
          <div className="fed-share-option">
            <button type="button" className="button" disabled={!directLink} aria-describedby={!directLink?"fed-direct-note":undefined}
              onClick={directLink ? ()=>run(async()=>{
                await copyText(directLink);notify("已复制直接连接链接");
              }) : undefined}>直接连接</button>
            {!directLink && <small id="fed-direct-note">暂无直连地址</small>}
          </div>
          <div className="fed-share-option">
            <button type="button" className="button" disabled={!share.rtc} aria-describedby={rtcHint?"fed-signaling-note":undefined}
              onClick={()=>run(async()=>{
                const invite=await request<{link:string}>("/peers/windows/"+selection.windowId+"/signaling-invitation",{method:"POST"});
                await copyText(invite.link);notify("已复制跨内网链接，15 分钟内可领取");
              })}>跨内网连接</button>
            {rtcHint && <small id="fed-signaling-note">{rtcHint}</small>}
          </div>
        </div>
      : <Provider key={selection.windowId} id={selection.windowId} run={run}/>)}
    {contact && <><b>{contact.nodeName} · {contact.online?"在线":"离线"}</b>
      <p>移除后，@{contact.name} 将收到通知，并失去向 @{owner} 请求协作的权限。</p>
      {contact.canRemove ? <button className="button danger" onClick={()=>run(async()=>{
        await request("/peers/windows/"+contact.windowId+"/contacts/"+contact.id,{method:"DELETE"});
        ref.current?.close();
      })}>移除协作</button> : <small>请先将申请方工作台更新到支持按 Agent 移除权限的版本。</small>}</>}
    {remote && <><b>{remote.nodeName} · {remote.nodeId.slice(0,8)}</b><span>{bindingDenied(remote)?"无权限":states[remote.status]} · {remote.connected?(remote.online?"Agent 在线":"Agent 离线"):"未连接"}</span>
      {remote.status==="approved" && !bindingDenied(remote) && <span className="remote-quota-detail">{quotaText(remote,true)}</span>}
      {remote.status==="approved" && <ReturnAgentSelect topicId={remote.topicId} value={remote.contactAgentId??""} onChange={agentId=>run(()=>put("remotes/"+remote.id+"/contact",{topicId:remote.topicId,agentId:agentId||null}))}/>}
      {remote.error && <small>{remote.error}</small>}
      <button className="button" onClick={()=>run(async()=>{await request("/peers/remotes/"+remote.id+"?topic="+remote.topicId,{method:"DELETE"});ref.current?.close();})}>移除本地胶囊</button></>}
    </fieldset>{note && (note.startsWith("已复制")
      ? <p role="status" className={share?"fed-share-feedback":undefined}>{note}</p>
      : <p role="alert" className={share?"form-error fed-share-feedback":"form-error"}>{note}</p>)}
  </dialog>;
}

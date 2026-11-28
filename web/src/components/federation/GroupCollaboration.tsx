import {useEffect,useRef,useState,type FormEvent} from "react";
import {useWorkbench,type Topic,type WindowView} from "../../store/workbenchStore";
import {request} from "../../services/api";
import {groupStates,openGroupCollaboration,type GroupCollaborationView,type GroupPolicy} from "../../services/permanentGroups";
import {copyText} from "../../services/clipboard";
import {LocalGroupLinks,GroupMembers} from "./LocalGroupLinks";
import {openShareSettings} from "./Federation";
import "./groupCollaboration.css";

const policyDefaults:GroupPolicy={permissionMode:"read_only",quotaMode:"daily",quotaLimit:20,paused:false,allowUpload:true};
const available=(group:GroupCollaborationView)=>group.status!=="revoked"&&group.status!=="rejected";
const draftKey=(topic:string)=>"ff.group-invite."+topic;
function invitationOptions(topic:string):{expiresAt?:number|null;memberMode?:string;members:string[];policy:GroupPolicy;purpose:string}|null {
  try { return JSON.parse(localStorage.getItem(draftKey(topic))??"null"); }
  catch { return null; }
}

function invitationPreview(value:string) {
  try {
    const url=new URL(value.trim());
    if (url.protocol!=="ffcp:" || !["/share/","/signal/"].some(prefix=>url.pathname.startsWith(prefix))) return null;
    const name=url.searchParams.get("ffGroup")?.slice(0,100);
    const purpose=url.searchParams.get("ffPurpose")?.slice(0,300);
    const suggestedMember=url.searchParams.get("ffMember")??(!name?url.searchParams.get("window"):null);
    url.searchParams.delete("ffMember");
    url.searchParams.delete("ffGroup");url.searchParams.delete("ffPurpose");
    return {url:url.href,name,purpose,suggestedMember};
  } catch {return null;}
}

export function GroupCollaborationDialog() {
  const {topic,snapshot}=useWorkbench();
  const dialog=useRef<HTMLDialogElement>(null);
  const [selected,select]=useState<string>();
  const [member,setMember]=useState<string>();
  const [tab,setTab]=useState<"local"|"external">("local");
  const [own,setOwn]=useState("");
  const [action,setAction]=useState<"invite"|"join">("invite");
  const [opening,setOpening]=useState(0);
  useEffect(()=>{
    const open=(event:Event)=>{
      const detail=(event as CustomEvent<{id?:string;tab:"local"|"external";member?:string}>).detail;
      select(detail.id);setMember(detail.member);setTab(detail.tab);setOwn(topic?.id??"");
      setAction("invite");setOpening(value=>value+1);
      if (!dialog.current?.open) dialog.current?.showModal();
    };
    window.addEventListener("ff:group-collaboration",open);
    return ()=>window.removeEventListener("ff:group-collaboration",open);
  },[topic?.id]);
  const groups=snapshot?.federation?.groups??[];
  const link=groups.find(group=>group.id===selected);
  const ownGroups=snapshot?.topics.filter(item=>item.status==="active")??[];
  const current=link?snapshot?.topics.find(item=>item.id===link.topic):
    ownGroups.find(item=>item.id===own)??ownGroups[0];
  const visible=groups.filter(group=>group.topic===current?.id&&available(group));
  const pending=visible.filter(group=>!group.requester&&group.status==="pending");
  const close=()=>dialog.current?.close();
  const start=(next:"invite"|"join")=>{if(link)setOwn(link.topic);select(undefined);setAction(next);};
  return <dialog ref={dialog} id="group-collaboration-dialog" aria-labelledby="group-collaboration-title" closedby="any">
    <header className="dialog-header"><h2 id="group-collaboration-title">小组合作</h2>
      <button type="button" className="icon-button" aria-label="关闭小组合作" onClick={close}>×</button>
    </header>
    <div className="group-collab-body">
      <nav className="group-collab-tabs" aria-label="合作范围">
        <button type="button" className="text-button" aria-pressed={tab==="local"} onClick={()=>setTab("local")}>本机小组</button>
        <button type="button" className="text-button" aria-pressed={tab==="external"} onClick={()=>setTab("external")}>外部小组{pending.length>0&&<small>{pending.length}</small>}</button>
      </nav>
      {tab==="local" ? topic&&<LocalGroupLinks key={topic.id+opening} topic={topic} done={close}/> : <>
        <nav className="group-collab-list" aria-label="外部合作操作">
          <button type="button" className="text-button" aria-pressed={!link&&action==="invite"} onClick={()=>start("invite")}>创建邀请</button>
          <button type="button" className="text-button" aria-pressed={!link&&action==="join"} onClick={()=>start("join")}>接受邀请</button>
        </nav>
        {!link&&ownGroups.length>0&&<label className="group-collab-own">我的小组
          <select value={current?.id??""} onChange={e=>{setOwn(e.target.value);select(undefined);}}>
            {ownGroups.map(group=><option key={group.id} value={group.id} title={group.project}>{group.name}{!group.leaderWindowId&&" · 待设组长"}</option>)}
          </select>
        </label>}
        {(visible.length>0||pending.length>0)&&<nav aria-label="外部合作记录" className="group-collab-list">
          {[...pending,...visible.filter(group=>!pending.some(item=>item.id===group.id))].map(group=>
            <button key={group.id} type="button" className="text-button" aria-pressed={selected===group.id} onClick={()=>select(group.id)}>
              {[group.remote.name,group.user].filter(Boolean).join(" · ")||"外部小组"}<small>{groupStates[group.status]}</small>
            </button>)}
        </nav>}
        {selected&&!link?<p role="status">正在加载合作记录…</p>:current&&current.status==="active"&&current.leaderWindowId?
          <GroupCollabForm key={opening+":"+(link?.id??current.id+":"+action)} action={action} topic={current} group={link} suggested={member}
            roster={snapshot?.windows??[]} done={select} shareSettings={()=>{close();openShareSettings(current.leaderWindowId!);}}/>:
          <p>请先为进行中的小组设置组长。</p>}
      </>}
    </div>
  </dialog>;
}

function GroupCollabForm({action,topic,group,suggested,roster,done,shareSettings}:{suggested?:string;action:"invite"|"join";topic:Topic;group?:GroupCollaborationView;roster:WindowView[];
    done:(id?:string)=>void;shareSettings:()=>void}) {
  const incoming=!!group&&!group.requester&&group.status==="pending";
  const [preset]=useState(()=>{
    if (!incoming) return null;
    const saved=invitationOptions(topic.id);
    return incoming && saved?.purpose!==group?.purpose ? null : saved;
  });
  const creating=!group&&action==="invite";
  const [link,setLink]=useState(""),[purpose,setPurpose]=useState(creating?(preset?.purpose??""):""),[busy,setBusy]=useState(false);
  const suggestedId=incoming?group?.suggestedMember:!group?suggested:undefined;
  const [members,setMembers]=useState<string[]>(suggestedId?[suggestedId]:preset?.members??group?.members??[]);
  const [memberMode,setMemberMode]=useState(suggestedId?"selected":preset?(preset.memberMode??"selected"):incoming?"all":group?(group.memberMode==="all"?"all":"selected"):"all");
  const [policy,setPolicy]=useState<GroupPolicy>(preset?.policy??group?.policy??policyDefaults);
  const [expires,setExpires]=useState(()=>{
    const time=preset?.expiresAt??group?.expiresAt;
    if(!time) return "";
    const date=new Date(time*1000);
    return new Date(date.getTime()-date.getTimezoneOffset()*60000).toISOString().slice(0,16);
  });
  const [confirmWrite,setConfirmWrite]=useState(group?.policy.permissionMode==="read_write");
  const [feedback,setFeedback]=useState("");
  const [needsSharing,setNeedsSharing]=useState(false);
  const preview=!group&&!creating?invitationPreview(link):null;
  const editable=!group||incoming||group.policy.status==="approved"&&available(group);
  async function commit(event:FormEvent,decision=group?(incoming?"approve":"configure"):"approve") {
    event.preventDefault();
    if (busy) return;
    setBusy(true);
    setFeedback("");
    const selection={memberMode,expiresAt:expires?Math.floor(new Date(expires).getTime()/1000):null,suggestedMember:preview?.suggestedMember??undefined,members:members.filter(id=>roster.some(view=>view.agent.id===id&&view.agent.topicId===topic.id)),policy:{...policy,action:decision,confirmWrite}};
    try {
      if (editable && decision!=="reject" && decision!=="revoke" && memberMode==="selected" && !selection.members.length) throw new Error("请选择参与合作的 Agent。");
      if (group) {
        await request("/peers/groups/"+group.id,{method:"PUT",body:selection});
        if (decision==="reject"||decision==="revoke") done(undefined);
        else setFeedback("已保存");
      } else if (creating) {
        localStorage.setItem(draftKey(topic.id),JSON.stringify({memberMode,expiresAt:selection.expiresAt,members:selection.members,policy,purpose:purpose.trim()}));
        const route="/peers/windows/"+topic.leaderWindowId;
        const share=await request<{enabled:boolean;rtc:boolean;link:string|null;dedicatedReadOnly:boolean}>(route);
        if (!share.enabled) {setNeedsSharing(true);throw new Error("开启本组组长的外部分享后即可创建邀请。");}
        if (share.dedicatedReadOnly) {setNeedsSharing(true);throw new Error("请先关闭组长的专用审阅模式，再创建小组邀请。");}
        const shared=share.rtc?(await request<{link:string}>(route+"/signaling-invitation",{method:"POST"})).link:share.link;
        if (!shared) throw new Error("暂无可用连接，请检查工作台的节点地址或信令设置。");
        const invite=new URL(shared);
        // 名称和用途仅用于粘贴时预览。名单与权限在两端确认后由现有握手同步。
        invite.searchParams.set("ffGroup",topic.name);
        invite.searchParams.set("ffPurpose",purpose.trim());
        if(memberMode==="selected" && selection.members.length===1) invite.searchParams.set("ffMember",selection.members[0]);
        await copyText(invite.href);
        setFeedback(share.rtc?"跨内网邀请已复制，请对方在 15 分钟内粘贴接受。":"邀请已复制，可发给对方在「接受邀请」中粘贴。");
      } else {
        if (!preview) throw new Error("请粘贴有效的小组邀请链接。");
        const result=await request<{id:string}>("/peers/groups",{method:"POST",body:{topicId:topic.id,link:preview.url,purpose,selection}});
        done(result.id);
      }
    } catch(error) { setFeedback((error as Error).message); }
    finally { setBusy(false); }
  }
  return <form className="group-collab-form" onSubmit={event=>void commit(event)}>
    <strong>{topic.name}{group?.remote.name?" ↔ "+group.remote.name:""}</strong>
    {!group?<>{!creating&&<><label>粘贴收到的邀请<input required value={link} onChange={event=>{
      const value=event.target.value;
      setLink(value);setPurpose(invitationPreview(value)?.purpose??"");
    }} autoComplete="off"/></label>
      {preview?.name&&<div className="group-collab-summary">邀请来源 · {preview.name}</div>}</>}
      <label>合作用途<input required maxLength={300} value={purpose} onChange={event=>setPurpose(event.target.value)}/></label></>:
      <div className="group-collab-summary"><span>{groupStates[group.status]}{group.user?" · "+group.user:""}</span>
        <p>{group.purpose}</p>{group.remote.members.length>0&&<p>对方参与成员 · {group.remote.members.map(member=>"@"+member.name).join("、")}</p>}
        {group.remoteQuota?.remaining!=null&&<small>对端剩余 {group.remoteQuota.remaining} 次</small>}</div>}
    {editable&&<>
      <GroupMembers topic={topic} roster={roster} selected={members} mode={memberMode} onMode={setMemberMode} onChange={setMembers} disabled={busy}/>
      <details className="group-collab-policy" open={policy.permissionMode==="read_write"||undefined}>
        <summary>权限与配额</summary>
      <fieldset><legend>接收对方消息的权限</legend>
        <div className="group-collab-quota"><label>权限<select value={policy.permissionMode} onChange={event=>{setConfirmWrite(false);setPolicy({...policy,permissionMode:event.target.value});}}>
          <option value="read_only">只读审阅</option><option value="read_write">允许修改</option></select></label>
          <label>次数配额<select value={policy.quotaMode} onChange={event=>setPolicy({...policy,quotaMode:event.target.value})}>
            <option value="daily">每日</option><option value="hourly">每小时</option><option value="total">总次数</option><option value="unlimited">不限次数</option></select></label>
          {policy.quotaMode!=="unlimited"&&<label>上限<input type="number" min={1} required value={policy.quotaLimit??""}
            onChange={event=>setPolicy({...policy,quotaLimit:event.target.valueAsNumber||null})}/></label>}
        </div>
        {policy.permissionMode==="read_write"&&<label className="group-collab-check"><input type="checkbox" required checked={confirmWrite}
          onChange={event=>setConfirmWrite(event.target.checked)}/>确认允许对方提出修改任务</label>}
        <label>合作截止时间（可选）<input type="datetime-local" value={expires} onChange={e=>setExpires(e.target.value)}/></label>
        <div className="group-collab-checks"><label><input type="checkbox" checked={policy.allowUpload}
          onChange={event=>setPolicy({...policy,allowUpload:event.target.checked})}/>允许附件</label>
          {group&&!incoming&&<label><input type="checkbox" checked={policy.paused}
            onChange={event=>setPolicy({...policy,paused:event.target.checked})}/>暂停协作</label>}</div>
      </fieldset>
      </details>
    </>}
    <div className="group-collab-actions">
      <span role="status">{feedback}</span>
      {needsSharing&&<button type="button" className="button" onClick={shareSettings}>打开组长分享设置</button>}
      {editable&&<button type="submit" className="button primary" disabled={busy}>
        {!group?(creating?"复制小组邀请":"提交本组名单"):incoming?"确认合作":"保存"}
      </button>}
      {group&&available(group)&&<button className="button danger" type="button" disabled={busy}
        onClick={event=>{if(incoming||window.confirm("解除与此小组的合作？"))void commit(event,incoming?"reject":"revoke");}}>
        {incoming?"拒绝":"解除协作"}
      </button>}
    </div>
  </form>;
}

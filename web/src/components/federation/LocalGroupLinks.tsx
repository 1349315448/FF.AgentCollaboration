import {useState,type FormEvent} from "react";
import {useWorkbench,type Topic,type WindowView} from "../../store/workbenchStore";
import {request} from "../../services/api";
import {eligibleLocalGroups,groupLinkSides} from "../../services/groupLinks";

export interface LocalSelection {group:string; enabled:boolean; members:string[]; peerMembers:string[]; mode:string; peerMode:string}

export function GroupMembers({topic,roster,selected,mode,label="本组参与成员",disabled,onChange,onMode}:{
  topic:Topic; roster:WindowView[]; selected:string[]; label?:string; disabled?:boolean; mode:string;
  onMode:(mode:string)=>void; onChange:(next:string[])=>void
}) {
  return <fieldset className="group-members-panel" disabled={disabled}><legend>{label}</legend>
    <select aria-label={label+"范围"} value={mode} onChange={e=>onMode(e.target.value)}>
      <option value="all">全组参与</option><option value="selected">指定成员</option>
    </select>
    {mode==="selected"&&<div className="group-collab-members">{roster.filter(view=>view.agent.topicId===topic.id).map(({agent})=>
      <label key={agent.id}><input type="checkbox" checked={selected.includes(agent.id)}
        onChange={e=>onChange(e.target.checked?[...selected,agent.id]:selected.filter(id=>id!==agent.id))}/>
        <span>@{agent.name}{agent.id===topic.leaderWindowId&&<small>组长</small>}</span></label>)}</div>}
  </fieldset>;
}

export function LocalGroupLinks({topic,done}:{topic:Topic;done:()=>void}) {
  const {snapshot}=useWorkbench();
  const groups=eligibleLocalGroups(snapshot,topic);
  const links=groupLinkSides(snapshot?.groupLinks,topic.id);
  const [changes,setChanges]=useState<Record<string,LocalSelection>>({});
  const [selected,select]=useState<string>();
  const [busy,setBusy]=useState(false),[error,setError]=useState("");
  const group=groups.find(item=>item.id===selected)??groups.find(item=>links.some(link=>link.id===item.id))??groups[0];
  function value(id:string):LocalSelection {
    const original=links.find(link=>link.id===id);
    const side=(ids:string[],mode:string|undefined,leader?:string|null)=>
      mode==="legacy" && leader?[...new Set([...ids,leader])]:ids;
    return changes[id]??{group:id,enabled:!!original,mode:original?(original.mode==="all"?"all":"selected"):"all",
      peerMode:original?(original.peerMode==="all"?"all":"selected"):"all",
      members:side(original?.members??[],original?.mode,topic.leaderWindowId),
      peerMembers:side(original?.peerMembers??[],original?.peerMode,groups.find(g=>g.id===id)?.leaderWindowId)};
  }
  const selection=group&&value(group.id);
  const update=(id:string,next:Partial<LocalSelection>)=>setChanges({...changes,[id]:{...value(id),...next}});
  async function save(event:FormEvent) {
    event.preventDefault();
    setBusy(true);setError("");
    // 已移除或调出的成员可能还在原名单里，只提交当前两组的有效 ID。
    const roster=snapshot?.windows??[];
    const membersFor=(ids:string[],groupId:string)=>ids.filter(id=>roster.some(view=>view.agent.id===id&&view.agent.topicId===groupId));
    try {
      const input=Object.values(changes).map(item=>({...item,memberMode:item.mode,members:membersFor(item.members,topic.id),peerMembers:membersFor(item.peerMembers,item.group)}));
      await request("/topics/"+topic.id+"/chat/links",{method:"PUT",body:input});
      done();
    } catch(reason) {setError((reason as Error).message);}
    finally {setBusy(false);}
  }
  return <form className="group-collab-form" onSubmit={save}>
    <div className="group-collab-context"><strong>{topic.name}</strong><small>勾选要合作的小组。双方默认全组参与。</small></div>
    {groups.length===0 ? <p>当前项目还没有其他进行中的小组。</p> :
      <div className="group-link-grid">
        <div className="group-link-list" aria-label="本机小组">{groups.map(item=>{
          const state=value(item.id);
          return <div key={item.id} className="group-link-row" data-selected={item.id===group?.id}>
            <input type="checkbox" aria-label={"关联 "+item.name} checked={state.enabled}
              disabled={busy||!state.enabled&&(!topic.leaderWindowId||!item.leaderWindowId)}
              onChange={e=>{select(item.id);update(item.id,{enabled:e.target.checked});}}/>
            <button type="button" className="text-button" onClick={()=>select(item.id)} aria-pressed={item.id===group?.id}>
              <span>{item.name}</span>
              {!item.leaderWindowId?<small>待设组长</small>:state.enabled&&links.some(link=>link.id===item.id)&&<small>已关联</small>}
            </button>
          </div>;
        })}</div>
        <div className="group-link-detail">
          {!topic.leaderWindowId ? <p>请先为「{topic.name}」设置组长。</p> :
            group&&!group.leaderWindowId ? <p>「{group.name}」还未设置组长。</p> :
            group&&selection?.enabled ? <>
              <GroupMembers topic={topic} roster={snapshot?.windows??[]} selected={selection.members} mode={selection.mode} onMode={mode=>update(group.id,{mode})} disabled={busy}
                label={topic.name+" · 参与成员"} onChange={members=>update(group.id,{members})}/>
              <GroupMembers topic={group} roster={snapshot?.windows??[]} selected={selection.peerMembers} mode={selection.peerMode} onMode={peerMode=>update(group.id,{peerMode})} disabled={busy}
                label={group.name+" · 参与成员"} onChange={peerMembers=>update(group.id,{peerMembers})}/>
            </> : <p>勾选「{group?.name}」后即可设置双方成员。</p>}
        </div>
      </div>}
    <div className="group-collab-actions"><span role="alert">{error}</span>
      <button type="submit" className="button primary" disabled={busy||!Object.keys(changes).length}>{busy?"正在保存…":"保存合作设置"}</button>
    </div>
  </form>;
}

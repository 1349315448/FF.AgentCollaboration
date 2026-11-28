import {useState,type ReactNode} from "react";
import {useWorkbench,type Topic,type WindowView} from "../../store/workbenchStore";
import {groupLinkSides,includedMember} from "../../services/groupLinks";
import {openGroupCollaboration} from "../../services/permanentGroups";
import {navigation,activityLabels,peerPresence} from "../../services/navigation";
import {engineNames} from "../../services/engines";
import {RemoteCapsule,viewReturnContact} from "../federation/Federation";
import {AgentNavigationItem} from "./AgentNavigationItem";
import {WorkspaceIcon} from "./WorkspaceIcon";

interface Cooperation {
  id:string;
  label:string;
  people:ReactNode;
  owners?:string[];
  open?:()=>void;
}

function Branch({name,children,link}:{name:string;children:ReactNode;link?:string}) {
  return <li><details open data-collab-branch={link}><summary title={name}>{name}</summary><ul>{children}</ul></details></li>;
}

function Person({id,name,status,activity,engine,leader,open}:{id:string;name:string;status:string;activity:string;
  engine?:string;leader?:boolean;open:()=>void}) {
  return <li><button type="button" className="tree-group-person" data-remote-id={id} data-activity={activity}
    title={"@"+name+" · "+status} onDoubleClick={open} onClick={e=>{if(e.detail===0)open();}}>
    <span className="nav-dot"/><strong>@{name}</strong>{leader&&<WorkspaceIcon name="flag"/>}
    {engine&&<small>{engineNames[engine]??engine}</small>}<small className="tree-group-status">{status}</small>
  </button></li>;
}

function OwnMember({view,cooperations}:{view:WindowView;cooperations:Cooperation[]}) {
  const [expanded,setExpanded]=useState(false);
  return <li data-cooperation-owner={view.agent.id}>
    <div className="tree-agent-line">
      <AgentNavigationItem view={view} tree/>
      {cooperations.length>0&&<button type="button" className="tree-cooperation-toggle" aria-expanded={expanded}
        title={"协作中 "+cooperations.length} aria-label={"@"+view.agent.name+" 协作中 "+cooperations.length}
        onClick={()=>setExpanded(value=>!value)}><WorkspaceIcon name="network"/>{cooperations.length}</button>}
    </div>
    {cooperations.length>0&&<ul className="tree-loans" hidden={!expanded}>
      {cooperations.map(item=><li key={item.id} data-loan-link={item.id}>
        <button type="button" className="tree-loan-group" title={item.label} onClick={item.open}>
          <WorkspaceIcon name="network"/><span>{item.label}</span>
        </button>
        <ul>{item.people}</ul>
      </li>)}
    </ul>}
  </li>;
}

export function CooperationTree({topic,members}:{topic:Topic;members:WindowView[]}) {
  const {snapshot}=useWorkbench();
  const cooperations:Cooperation[]=[];
  for(const link of groupLinkSides(snapshot?.groupLinks,topic.id)) {
    const other=snapshot?.topics.find(t=>t.id===link.id&&t.status==="active");
    if(!other)continue;
    const eligible=snapshot?.windows.filter(w=>w.agent.topicId===link.id&&
      includedMember(link.peerMode,link.peerMembers,w.agent.id,!!w.agent.isLeader))??[];
    const people=eligible.sort((a,b)=>Number(b.agent.online)-Number(a.agent.online)).map(({agent,activity})=>
      <Person key={agent.id} id={agent.id} name={agent.name} leader={agent.isLeader} engine={agent.engine} activity={activity}
        status={activityLabels[activity]} open={()=>navigation.selectAgent(other.id,agent.id)}/>);
    const owners=link.mode==="selected"?link.members:undefined;
    cooperations.push({id:link.id,label:other.name+" · 本机",people,owners,open:()=>openGroupCollaboration(undefined,"local")});
  }
  for(const group of snapshot?.federation?.groups??[]) {
    if(group.topic!==topic.id||["revoked","rejected"].includes(group.status))continue;
    const label=[group.remote.name,group.user].filter(Boolean).join(" · ")||"待确认的小组";
    const open=()=>openGroupCollaboration(group.id);
    const people=group.recipients.filter(person=>person.engine).sort((a,b)=>Number(b.online)-Number(a.online)).map(person=>
      <Person key={person.key} id={person.key} name={person.name} engine={person.engine} leader={person.leader}
        activity={person.online?"idle":"offline"} status={peerPresence(person.online,group.connected)} open={open}/>);
    const owners=!group.requester&&group.memberMode==="selected"&&group.members.length ? group.members : undefined;
    cooperations.push({id:group.id,label,people:people.length?people:
      <li><button type="button" className="text-button" onClick={open}>查看合作</button></li>,owners,open});
  }
  for(const remote of snapshot?.federation?.remotes??[]) {
    if(remote.topicId!==topic.id)continue;
    const label=[remote.groupName,remote.nodeName].filter(Boolean).join(" · ")||"外部协作";
    cooperations.push({id:remote.id,label,people:<li><RemoteCapsule remote={remote} tree/></li>});
  }
  for(const contact of snapshot?.federation?.contacts??[]) {
    if(!members.some(view=>view.agent.id===contact.windowId))continue;
    const open=()=>viewReturnContact(contact.id);
    cooperations.push({id:contact.grantId??contact.id,label:contact.nodeName,owners:[contact.windowId],open,
      people:<Person id={contact.id} name={contact.name} activity={contact.online?"idle":"offline"}
        status={peerPresence(contact.online,contact.connected)} open={open}/>});
  }
  const own=[...members].sort((a,b)=>Number(b.agent.online)-Number(a.agent.online)).map(view=>
    <OwnMember key={view.agent.id} view={view} cooperations={cooperations.filter(item=>item.owners?.includes(view.agent.id))}/>);
  const branches=cooperations.filter(item=>!item.owners);
  return <ul className="tree-members">{cooperations.length?<><Branch name="本组成员" link="own">{own}</Branch>
    {branches.length>0&&<Branch name="协作小组" link="partners">{branches.map(item=>
      <Branch key={item.id} name={item.label} link={item.id}>{item.people}</Branch>)}</Branch>}</>:own}</ul>;
}

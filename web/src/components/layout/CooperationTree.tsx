import {useState,type ReactNode} from "react";
import {useWorkbench,type Topic,type WindowView} from "../../store/workbenchStore";
import {groupLinkSides,includedMember} from "../../services/groupLinks";
import {groupStates,openGroupCollaboration} from "../../services/permanentGroups";
import {navigation,activityLabels,peerPresence,remoteOnline} from "../../services/navigation";
import {engineNames} from "../../services/engines";
import {RemoteCapsule,viewReturnContact} from "../federation/Federation";
import {AgentNavigationItem} from "./AgentNavigationItem";
import {WorkspaceIcon} from "./WorkspaceIcon";

interface Cooperation {
  id:string;
  label:string;
  people:ReactNode;
  status?:string;
  owners?:string[];
  open?:()=>void;
}

function Branch({name,children,link}:{name:string;children:ReactNode;link?:string}) {
  return <li><details open data-collab-branch={link}><summary title={name}>{name}</summary><ul>{children}</ul></details></li>;
}

function CooperationLink({item}:{item:Cooperation}) {
  return <button type="button" className="tree-loan-group" data-collab-branch={item.id}
    title={[item.label,item.status,"查看合作详情"].filter(Boolean).join(" · ")} onClick={item.open}>
    <WorkspaceIcon name="network"/><span>{item.label}</span>{item.status&&<small>{item.status}</small>}
  </button>;
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
        <CooperationLink item={item}/>
        {item.people&&<ul>{item.people}</ul>}
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
    const status=group.status==="approved" ? (group.connected?"暂无参与成员":"成员未同步") : groupStates[group.status];
    cooperations.push({id:group.id,label,people:people.length?people:null,status:people.length?undefined:status,owners,open});
  }
  for(const contact of snapshot?.federation?.contacts??[]) {
    if(!members.some(view=>view.agent.id===contact.windowId))continue;
    const open=()=>viewReturnContact(contact.id);
    cooperations.push({id:contact.grantId??contact.id,label:contact.nodeName,owners:[contact.windowId],open,
      people:<Person id={contact.id} name={contact.name} activity={contact.online?"idle":"offline"}
        status={peerPresence(contact.online,contact.connected)} open={open}/>});
  }
  const remotes=snapshot?.federation?.remotes.filter(remote=>remote.topicId===topic.id)??[];
  const own=[
    ...members.map(view=>({online:view.agent.online,row:<OwnMember key={view.agent.id} view={view}
      cooperations={cooperations.filter(item=>item.owners?.includes(view.agent.id))}/>})),
    ...remotes.map(remote=>({online:remoteOnline(remote),row:<li key={"remote-"+remote.id}><RemoteCapsule remote={remote} tree/></li>}))
  ].sort((a,b)=>Number(b.online)-Number(a.online)).map(item=>item.row);
  const branches=cooperations.filter(item=>!item.owners);
  return <ul className="tree-members">{cooperations.length||remotes.length?<><Branch name="本组成员" link="own">{own}</Branch>
    {branches.length>0&&<Branch name="协作小组" link="partners">{branches.map(item=>
      item.people ? <Branch key={item.id} name={item.label} link={item.id}>{item.people}</Branch> :
        <li key={item.id}><CooperationLink item={item}/></li>)}</Branch>}</>:own}</ul>;
}

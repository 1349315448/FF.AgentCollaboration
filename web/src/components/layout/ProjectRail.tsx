import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { useWorkbench, type Topic, type ProjectTotals } from "../../store/workbenchStore";
import { navigation, projectKey, remoteOnline } from "../../services/navigation";
import { fitProjectPath } from "../../services/projectPath";
import {CooperationTree} from "./CooperationTree";
import { NavigationName } from "./NavigationName";
import { WorkspaceIcon } from "./WorkspaceIcon";
import { ProjectKnowledgeDialog } from "../dialogs/ProjectKnowledgeDialog";
import { knowledgeUiLocal } from "../../services/knowledge";

// A selected path opens on page load. Remounting its parent preserves later user choices.
const touchedBranches = new Set<string>();

function useBranch(key:string, selected:boolean) {
  const storageKey = "lx.agent.workbench.tree."+key;
  const [expanded, set] = useState(()=>{
    if (selected && !touchedBranches.has(key)) return true;
    try { return localStorage.getItem(storageKey)==="true"; }
    catch { return selected; }
  });
  const wasSelected=useRef(selected);
  function change(value:boolean) {
    touchedBranches.add(key);
    set(value);
    try { localStorage.setItem(storageKey,String(value)); } catch { /* 当前页面仍可正常展开。 */ }
  }
  useEffect(()=>{
    if (selected && !wasSelected.current) change(true);
    wasSelected.current=selected;
  },[selected]);
  return {expanded, toggle:()=>change(!expanded)};
}

function TopicBranch({topic}:{topic:Topic}) {
  const {snapshot,rosters,selectedTopicId}=useWorkbench();
  const selected=topic.id===selectedTopicId;
  const {expanded,toggle}=useBranch("topic."+topic.id,selected);
  const {members, unread}=rosters.get(topic.id)!;
  const remotes=snapshot?.federation?.remotes.filter(r=>r.topicId===topic.id) ?? [];
  const online=members.filter(m=>m.agent.online).length + remotes.filter(remoteOnline).length;
  const row=useRef<HTMLDivElement>(null);
  useEffect(()=>{if (selected) row.current?.scrollIntoView({block:"nearest",inline:"nearest"});},[selected]);
  return <li data-nav-topic={topic.id}>
    <div ref={row} className="tree-row topic-choice" data-topic={topic.id} data-selected={selected} data-activity={topic.activity}
      onClick={event=>{if (!(event.target as Element).closest("button")) void navigation.switchTopic(topic.id);}}>
      <button type="button" className="tree-arrow" aria-label={(expanded?"收起":"展开")+topic.name+"的 Agent"} aria-expanded={expanded} onClick={toggle}>
        <WorkspaceIcon name={expanded?"down":"right"}/></button>
      <NavigationName className="topic-label" current={selected} title={topic.name} onSelect={()=>navigation.switchTopic(topic.id)}
        onRename={()=>navigation.editTopic(topic.id)}>{topic.name}</NavigationName>
      {unread>0 && <span className="tag-inbox" title={unread+" 条待处理"}>{unread}</span>}
      <small className="tree-total">{online}/{members.length+remotes.length}</small>
      <span className="tree-actions">
        <button type="button" onClick={()=>navigation.archiveTopic(topic.id)}>{topic.status==="finished"?"重开":"归档"}</button>
      </span>
    </div>
    {expanded && <CooperationTree topic={topic} members={members}/>}
  </li>;
}

function ProjectBranch({path,total,topics,onSettings}:{path:string;total:ProjectTotals;topics:Topic[];onSettings:()=>void}) {
  const {snapshot,topic}=useWorkbench();
  const key=projectKey(path), current=!!snapshot?.project && projectKey(snapshot.project)===key;
  const project=useBranch("project."+key,current);
  const archived=topics.filter(t=>t.status==="finished");
  const archive=useBranch("archive."+key,current && topic?.status==="finished");
  const name=useRef<HTMLElement>(null);
  useLayoutEffect(()=>{
    const el=name.current!;
    const fit=()=>fitProjectPath(el);
    fit();const observer=new ResizeObserver(fit);observer.observe(el);
    return ()=>observer.disconnect();
  },[path]);
  const topicIds=new Set(topics.map(t=>t.id));
  const remotes=new Map(snapshot?.federation?.remotes.filter(r=>topicIds.has(r.topicId)).map(r=>[r.id,r]));
  const active=total.agents-total.offline + [...remotes.values()].filter(remoteOnline).length;
  const description=path+" · "+total.topics+" 小组 · 工作 "+total.working+" · 空闲 "+total.idle+" · 离线 "+total.offline;
  return <li data-project={path}>
    <div className="tree-row project-button" data-key={key} id={current?"current-project":undefined}
      data-activity={(total.agents || remotes.size) && !active ? "offline" : total.activity} title={description+(knowledgeUiLocal?" · 双击打开项目设置":"")}
      onClick={event=>{if (!(event.target as Element).closest("button")) project.toggle();}}
      onDoubleClick={event=>{if (knowledgeUiLocal && !(event.target as Element).closest(".tree-actions")) onSettings();}}>
      <button type="button" className="project-select" aria-expanded={project.expanded} aria-label={description} onClick={project.toggle}
        onKeyDown={event=>{if (knowledgeUiLocal && event.key==="F2") {event.preventDefault(); onSettings();}}}>
        <WorkspaceIcon name={project.expanded?"down":"right"}/><span className="nav-dot"/>
        <strong ref={name} id={current?"project-name":undefined} data-path={path} title={path}/>
      </button>
      <small className="tree-total">{active}/{total.agents+remotes.size}</small>
      <span className="tree-actions">
        <button type="button" title="创建小组" aria-label={"在 "+path+" 创建小组"} onClick={()=>navigation.addTopic(path)}>＋</button>
        <button type="button" id={current?"remove-project":undefined} aria-label={current?"移除当前项目":"移除项目"} title={"移除 "+path} onClick={()=>navigation.removeProject(path)}><WorkspaceIcon name="close"/></button>
      </span>
    </div>
    {project.expanded && <ul>{topics.filter(t=>t.status!=="finished").map(t=><TopicBranch key={t.id} topic={t}/>)}
      {archived.length>0 && <li><button type="button" className="tree-archive" aria-expanded={archive.expanded} onClick={archive.toggle}>
        <WorkspaceIcon name={archive.expanded?"down":"right"}/>已归档 ({archived.length})</button>
        {archive.expanded && <ul>{archived.map(t=><TopicBranch key={t.id} topic={t}/>)}</ul>}</li>}
    </ul>}
  </li>;
}

export function ProjectRail() {
  const {snapshot,totals}=useWorkbench();
  const version=snapshot?.version?"Base v"+snapshot.version:"Base";
  const [settingsPath,setSettingsPath]=useState<string>();
  useEffect(()=>{
    const open=(event:Event)=>setSettingsPath((event as CustomEvent<string>).detail);
    window.addEventListener("ff:project-settings",open);
    return ()=>window.removeEventListener("ff:project-settings",open);
  },[]);
  const all=[...totals.values()].reduce((sum,t)=>({working:sum.working+t.working,idle:sum.idle+t.idle,offline:sum.offline+t.offline}),{working:0,idle:0,offline:0});
  return <aside className="project-sidebar" aria-label="项目">
    <div id="project-rail">
      <div className="project-tools"><strong>项目</strong><button type="button" className="text-button" id="add-project" aria-label="打开项目文件夹" disabled={!snapshot}>＋ 项目</button></div>
      <nav id="project-tabs" aria-label="项目、小组与 Agent"><ul>{snapshot?.projects.map(path=><ProjectBranch key={projectKey(path)} path={path} total={totals.get(projectKey(path))!} onSettings={()=>setSettingsPath(path)}
        topics={snapshot.topics.filter(t=>projectKey(t.project)===projectKey(path))}/>)}</ul></nav>
      <div className="project-states" title="本地 Agent 状态汇总"><span data-activity="working">工作 <b>{all.working}</b></span><span data-activity="idle">空闲 <b>{all.idle}</b></span><span data-activity="offline">离线 <b>{all.offline}</b></span></div>
      <footer className="brand">
        <button type="button" className="icon-button" id="open-settings" title="工作台配置与环境检测" aria-label="工作台配置与环境检测"><WorkspaceIcon name="settings"/></button>
        <span className="connection" id="connection" role="status" title="连接中" aria-label="连接中">连接中</span>
        <span className="brand-version" aria-label={"软件版本 "+version}>{version}</span>
      </footer>
    </div>
    {settingsPath && <ProjectKnowledgeDialog key={settingsPath} project={settingsPath} onClose={()=>setSettingsPath(undefined)}/>}
  </aside>;
}

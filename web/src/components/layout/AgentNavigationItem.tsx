import { Fragment } from "react";
import { navigation, remoteOnline } from "../../services/navigation";
import type { WindowView } from "../../store/workbenchStore";
import { RemoteCapsule, viewReturnContact, type Federation } from "../federation/Federation";
import { WorkspaceIcon } from "./WorkspaceIcon";
import { LeaderFlag } from "./LeaderFlag";
import { NavigationName } from "./NavigationName";
import { engineNames } from "../../services/engines";
import { useEngineQuota } from "../../services/engineQuota";

const states = {working:"工作", idle:"待命", waiting:"待处理", offline:"离线", finished:"已结束"};

function AgentNavigationItem({view, tree = false}:{view:WindowView; tree?:boolean}) {
  const {agent, activity, inbox} = view;
  const quota=useEngineQuota(agent.engine);
  const engine=engineNames[agent.engine] ?? agent.engine;
  const count = inbox.filter(m => m.state === "pending" || m.state === "delivery_uncertain").length;
  const uncertain = inbox.some(m => m.state === "delivery_uncertain");
  const power = (agent.online ? "关闭 @" : "启动 @") + agent.name;
  return <span className={tree ? "tree-agent" : "topic-agent-tag"} data-agent={agent.id} data-activity={activity}
    onClick={event => { if (!(event.target as Element).closest("button")) navigation.selectAgent(agent.topicId, agent.id); }}
    onDoubleClick={event => { if (tree && !(event.target as Element).closest("button")) navigation.renameAgent(agent.id); }}>
    <button type="button" className="tag-power" aria-label={power} title={power} onClick={()=>navigation.powerAgent(agent.id)}>
      <span className="nav-dot"/><svg viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5" aria-hidden="true"><path d="M8 1v6M4.9 3.5a5.5 5.5 0 1 0 6.2 0"/></svg>
    </button>
    <NavigationName className="member-name" title={"@"+agent.name+" · "+states[activity]} onSelect={()=>navigation.selectAgent(agent.topicId,agent.id)}
      onRename={()=>navigation.renameAgent(agent.id)}>{"@"+agent.name}</NavigationName>
    {agent.isLeader && <LeaderFlag />}
    <small className="nav-engine" title={engine}>{engine.replace(/^(GitHub |腾讯 |千问 |智谱 )/,"").replace(/ Code$/,"")}</small>
    <button type="button" className="nav-quota" title={quota?.title ?? "点击刷新额度"} aria-label={"刷新 @"+agent.name+" 的剩余额度"}
      disabled={quota?.loading} onClick={async()=>{const {loadQuota}=await import("../../runtime/quota.js");loadQuota(agent.engine,true);}}>[{quota?.value??"—"}]</button>
    {count > 0 && <span className="agent-mail-count" data-uncertain={uncertain} aria-label={count+" 条待处理投递"} title={uncertain?"含送达待核对的投递":"待处理投递"}>
      <WorkspaceIcon name="envelope"/>{count}</span>}
    {tree && <small className="tree-agent-state">{states[activity]}</small>}
  </span>;
}

export function TopicAgentList({members, remotes, contacts=[], tree = false}:{
  members:WindowView[]; remotes:Federation["remotes"]; contacts?:Federation["contacts"]; tree?:boolean;
}) {
  const entries = [
    ...members.map(view => ({
      key:"local-"+view.agent.id, online:view.agent.online, item:<AgentNavigationItem view={view} tree={tree}/>,
      children:tree?contacts.filter(c=>c.windowId===view.agent.id):[]
    })),
    ...remotes.map(remote => ({
      key:"remote-"+remote.id, online:remoteOnline(remote), item:<RemoteCapsule remote={remote} tree={tree}/>, children:[]
    }))
  ];
  entries.sort((a,b) => Number(b.online) - Number(a.online));
  const Row = tree ? "li" : Fragment;
  return entries.map(entry => <Row key={entry.key}>{entry.item}
    {entry.children.length>0 && <ul className="tree-return-contacts" aria-label="申请方 Agent">{entry.children.map(c=>
      <li key={c.id} data-contact={c.id}><button type="button" data-activity={c.online?"idle":"offline"}
        onClick={e=>{if(e.detail===0)viewReturnContact(c.id);}} onDoubleClick={()=>viewReturnContact(c.id)}
        title={`@${c.name} · ${c.nodeName} · 双击管理协作`}>
        <WorkspaceIcon name="network"/><span aria-hidden="true" className="nav-dot"/><span>@{c.name}</span><small>{c.nodeName}</small>
        <small className="tree-agent-state">{c.online?"在线":"离线"}</small></button>
      </li>)}</ul>}
  </Row>);
}

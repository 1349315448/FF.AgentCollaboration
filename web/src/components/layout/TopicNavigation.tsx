import { navigation, projectKey } from "../../services/navigation";
import { useWorkbench } from "../../store/workbenchStore";
import { TopicAgentList } from "./AgentNavigationItem";
import { NavigationName } from "./NavigationName";

export function TopicNavigation() {
  const {snapshot, topic, rosters} = useWorkbench();
  if (!topic) return <div className="topic-navigation"/>;
  return <nav className="topic-navigation" aria-label="当前话题">
    <div className="current-topic" data-activity={topic.activity}>
      <span className="nav-dot" aria-hidden="true"/>
      <NavigationName className="current-topic-title" title={topic.name} onRename={()=>navigation.editTopic(topic.id)}>{topic.name}</NavigationName>
      <select id="mobile-topic" aria-label="切换话题" value={topic.id} onChange={event=>navigation.switchTopic(event.target.value)}>
        {snapshot?.projects.map(path=><optgroup key={path} label={path}>{snapshot.topics.filter(t=>projectKey(t.project)===projectKey(path)).map(t=>
          <option key={t.id} value={t.id}>{t.name}{t.status==="finished"?"（已归档）":""}</option>)}</optgroup>)}
      </select>
    </div>
    <div className="topic-agents" id="topic-list" aria-label="当前话题的 Agent">
      <TopicAgentList members={rosters.get(topic.id)?.members ?? []} remotes={snapshot?.federation?.remotes.filter(r=>r.topicId===topic.id) ?? []}/>
      {topic.status==="active" && <button type="button" className="text-button" id="new-agent" aria-label="添加 Agent" onClick={navigation.addAgent}>＋ Agent</button>}
    </div>
  </nav>;
}

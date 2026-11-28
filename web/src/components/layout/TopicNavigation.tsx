import { navigation, projectKey } from "../../services/navigation";
import { useWorkbench } from "../../store/workbenchStore";

export function TopicNavigation() {
  const {snapshot, topic} = useWorkbench();
  if (!topic) return <div className="topic-navigation"/>;
  return <nav className="topic-navigation" aria-label="当前小组">
    <select id="mobile-topic" aria-label="切换小组" value={topic.id} onChange={event=>navigation.switchTopic(event.target.value)}>
      {snapshot?.projects.map(path=><optgroup key={path} label={path}>{snapshot.topics.filter(t=>projectKey(t.project)===projectKey(path)).map(t=>
        <option key={t.id} value={t.id}>{t.name}{t.status==="finished"?"（已归档）":""}</option>)}</optgroup>)}
    </select>
  </nav>;
}

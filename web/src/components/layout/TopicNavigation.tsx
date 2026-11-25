import { useLayoutEffect, useRef } from "react";
import { useWorkbench } from "../../store/workbenchStore";
import { activityLabels, navigation, projectKey } from "../../services/navigation";

export function TopicNavigation() {
  const { snapshot, selectedTopicId, rosters } = useWorkbench();
  const list = useRef<HTMLElement>(null);
  useLayoutEffect(() => {
    const nav = list.current;
    const active = nav?.querySelector('[aria-pressed="true"]');
    if (!nav?.clientWidth || !active) return;
    const tab = active.getBoundingClientRect(), strip = nav.getBoundingClientRect();
    if (tab.right > strip.right) nav.scrollLeft += Math.ceil(tab.right - strip.right);
    else if (tab.left < strip.left) nav.scrollLeft -= Math.ceil(strip.left - tab.left);
  }, [selectedTopicId]);
  const project = snapshot?.project && projectKey(snapshot.project);
  const topics = (snapshot?.topics ?? []).filter(t => projectKey(t.project) === project).map(topic => {
    const { members, unread } = rosters.get(topic.id)!;
    const detail = topic.name + " · " + activityLabels[topic.activity] + " · " + members.length + " 个 Agent";
    return { topic, members, unread, detail, title:detail + " · " + unread + " 条待处理" };
  });
  const groups = [topics.filter(t => t.topic.status !== "finished"), topics.filter(t => t.topic.status === "finished")];
  return <div className="topic-navigation">
    <nav id="topic-list" ref={list} aria-label="当前项目的话题">{groups.flat().filter(({topic}) => topic.status !== "finished" || topic.id === selectedTopicId).map(({topic,members,unread,title}) =>
      <button type="button" className="topic-choice" key={topic.id} data-topic={topic.id} data-finished={topic.status === "finished"}
        data-activity={topic.activity} title={title} aria-label={title} aria-pressed={topic.id === selectedTopicId} onClick={() => navigation.switchTopic(topic.id)}>
        <span className="topic-label">{topic.name}</span><span className="topic-running">进行中</span>
        <span className="topic-agents" hidden={!members.length}>{members.map(({agent,activity}) =>
          <span key={agent.id} className="topic-agent-tag" data-agent={agent.id} data-activity={activity}
            title={"@" + agent.name + " · " + activityLabels[activity]} onClick={e => {
              e.stopPropagation(); navigation.switchTopic(topic.id); navigation.focusAgent(agent.id);
            }}><span className="nav-dot" aria-hidden="true" /><span className="member-name">@{agent.name}</span></span>
        )}</span><span className="tag-inbox">{unread > 99 ? "99+" : unread || ""}</span><small className="topic-count">{members.length} 位</small>
      </button>
    )}</nav>
    <label className="topic-picker" title="全部话题"><select id="mobile-topic" aria-label="切换话题" disabled={!selectedTopicId} value={selectedTopicId || ""}
      onChange={e => navigation.switchTopic(e.target.value)}>{groups.map((group,i) =>
        <optgroup key={i} label={i ? "已结束" : "进行中"} hidden={!group.length}>
          {group.map(({topic,unread,title,detail}) => <option key={topic.id} value={topic.id}>{unread ? title : detail}</option>)}
        </optgroup>)}</select><span aria-hidden="true">▾</span></label>
    <button type="button" className="icon-button" id="topic-new" title="新建话题" aria-label="新建话题" disabled={!project}>＋</button>
  </div>;
}

import { CollapsedPanesTray } from "./CollapsedPanesTray";
import { useWorkbench } from "../../store/workbenchStore";

export function TopicHeader() {
  const { topic } = useWorkbench();
  const active = topic?.status === "active", finished = topic?.status === "finished";
  const working = topic?.activity === "working";
  return <header className="topic-heading" id="topic-heading" hidden={!topic}>
    <div className="topic-actions">
      <span id="topic-activity" hidden={!working} data-activity={topic?.activity || ""}>{working ? "进行中" : ""}</span>
      <button className="text-button" type="button" id="topic-edit" title="编辑话题">编辑</button>
      <button className="text-button" type="button" id="topic-state" title={finished ? "重新打开话题" : "结束并归档话题"}>{finished ? "重新打开" : "归档"}</button>
      <button className="text-button" type="button" id="new-agent" title="为当前话题添加 Agent" disabled={!active} hidden={!active}>＋ Agent</button>
    </div>
    <CollapsedPanesTray />
    <span id="active-topic-goal">{(finished && topic?.finishSummary) || topic?.goal || ""}</span>
  </header>;
}

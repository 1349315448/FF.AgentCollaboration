import { useWorkbench } from "../../store/workbenchStore";

export function EmptyWorkspace() {
  const { snapshot, topic, rosters } = useWorkbench();
  return <section id="empty" className="empty-workspace" hidden={!snapshot || !!(topic && rosters.get(topic.id)?.members.length)}>
    <div className="empty-content">
      <span className="empty-icon" aria-hidden="true">›_</span>
      <h2 id="empty-title">{topic ? "暂无 Agent" : snapshot?.project ? "暂无话题" : "尚未打开项目"}</h2>
      <p id="empty-description" hidden={!topic?.project}>{topic?.project}</p>
      <button id="empty-create" type="button" className="button primary" disabled={topic?.status === "finished"}>
        {topic ? "＋ Agent" : snapshot?.project ? "＋ 新建话题" : "打开项目文件夹"}
      </button>
    </div>
  </section>;
}

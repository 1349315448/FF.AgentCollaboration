import { useWorkbench } from "../../store/workbenchStore";

export function EmptyWorkspace() {
  const { snapshot, topic } = useWorkbench();
  return <section id="empty" className="empty-workspace" hidden={!snapshot || !!topic}>
    <div className="empty-content">
      <span className="empty-icon" aria-hidden="true">›_</span>
      <h2 id="empty-title">{snapshot?.project ? "暂无小组" : "尚未打开项目"}</h2>
      <p id="empty-description" hidden={!topic?.project}>{topic?.project}</p>
      <ul className="empty-capabilities"><li>多引擎并发协作</li><li>自动投递与回执</li><li>代码与变更审阅</li></ul>
      <button id="empty-create" type="button" className="button primary" disabled={topic?.status === "finished"}>
        {snapshot?.project ? "＋ 创建小组" : "打开项目文件夹"}
      </button>
    </div>
  </section>;
}

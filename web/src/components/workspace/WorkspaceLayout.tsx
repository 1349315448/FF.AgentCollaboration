import { useWorkbench } from "../../store/workbenchStore";

export function WorkspaceLayout() {
  const { topic, rosters } = useWorkbench();
  return (
    <div id="workspace-layout" className="workspace-layout" hidden={!topic || !rosters.get(topic.id)?.members.length}>
      <button id="return-to-chat" type="button" className="button compact"
        onClick={()=>window.dispatchEvent(new Event("ff:focus-chat"))}>返回群聊</button>
      <section id="primary-panes" className="primary-panes" aria-label="主对话" hidden></section>
      <div id="pane-divider" className="pane-divider" role="separator" tabIndex={0} aria-orientation="vertical"
        aria-label="调整主对话宽度" aria-controls="primary-panes" aria-valuemin={0} aria-valuemax={100} aria-valuenow={45} hidden></div>
      <section id="collaboration" className="collaboration" aria-label="协作区">
        <div className="collaboration-scroll">
          <section id="panes" className="panes" aria-label="协作终端"></section>
        </div>
      </section>
    </div>
  );
}

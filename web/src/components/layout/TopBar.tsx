import { ResourceManager } from "../dialogs/ResourceManager";
import { TopicNavigation } from "./TopicNavigation";
import { WorkspaceIcon } from "./WorkspaceIcon";
import { useGitWorkspace } from "../inspector/gitWorkspace";
import { layout } from "../../store/workbenchStore";

export function TopBar({toolbarRef}:{toolbarRef: React.RefCallback<HTMLDivElement>}) {
  const {status}=useGitWorkspace();
  const branch=status?.branch==="(detached)"?"游离 HEAD":status?.branch;
  const count=new Set(status?.changes?.map(change=>change.path)).size;
  return (
    <header className="topbar">
      <TopicNavigation />
      <div className="top-commands"><div className="top-actions">
        {branch&&<button type="button" id="git-status-slot" className="text-button"
          aria-label={"查看 Git 变更："+branch+" · "+count} title={branch+" · "+count+" 个变更文件"}
          onClick={()=>{
            if(layout?.workspaceMode!=="code") document.querySelector<HTMLButtonElement>('#workspace-mode-switch [data-mode="code"]')?.click();
            document.getElementById("inspector-tab-changes")?.click();
          }}><WorkspaceIcon name="changes"/><span>{branch}</span><small>{count}</small></button>}
        <div id="workspace-mode-switch" role="group" aria-label="工作区模式">
          <button type="button" data-mode="agent" aria-label="Agent 模式" aria-pressed="true" title="Agent 模式 · Ctrl+Shift+E"><WorkspaceIcon name="terminal"/><span>Agent 模式</span></button>
          <button type="button" data-mode="code" aria-label="代码模式" aria-pressed="false" title="代码模式 · Ctrl+Shift+E"><WorkspaceIcon name="code"/><span>代码模式</span></button>
        </div>
        <button id="toggle-inspector" type="button" className="icon-button" aria-label="代码速览" title="代码速览" aria-controls="inspector-shell" aria-expanded="false"><WorkspaceIcon name="panel"/></button>
      </div>
        <div className="top-tool-row">
          <div id="inspector-toolbar-host" ref={toolbarRef} hidden />
          <ResourceManager />
        </div>
      </div>
    </header>
  );
}

import { useEffect, useRef } from "react";
import { ResourceManager } from "../dialogs/ResourceManager";
import { TeamProposalControl } from "../dialogs/TeamProposalDialog";
import { useWorkbench } from "../../store/workbenchStore";
import { navigation } from "../../services/navigation";
import { knowledgeUiLocal } from "../../services/knowledge";
import { TopicNavigation } from "./TopicNavigation";
import { WorkspaceIcon } from "./WorkspaceIcon";

function press(id: string) { document.getElementById(id)?.click(); }

function showInspector(section: "files" | "docs" | "changes") {
  if (document.getElementById("inspector-shell")?.hidden) press("toggle-inspector");
  press("inspector-tab-" + section);
}

export function TopBar() {
  const {snapshot, topic} = useWorkbench();
  const menu = useRef<HTMLElement>(null);
  const project = topic?.project ?? snapshot?.project;

  function closeMenus(except?: HTMLDetailsElement) {
    menu.current?.querySelectorAll<HTMLDetailsElement>("details[open]").forEach(details=>{
      if (details!==except) details.open=false;
    });
  }
  useEffect(()=>{
    const outside = (event: PointerEvent) => {
      if (event.target instanceof Node && !menu.current?.contains(event.target)) closeMenus();
    };
    document.addEventListener("pointerdown",outside);
    return ()=>document.removeEventListener("pointerdown",outside);
  },[]);

  return <header className="topbar">
    <div className="platform-mark">
      <button type="button" className="icon-button" id="project-collapse" aria-controls="project-rail" aria-expanded="true"
        aria-label="收起项目栏"><img src="/platform-mark.svg" width="22" height="22" alt=""/></button>
      <span className="connection" id="connection" role="status" title="连接中" aria-label="连接中">连接中</span>
    </div>
    <nav className="workbench-menu" aria-label="工作台菜单" ref={menu}
      onClick={event=>{
        const button = (event.target as Element).closest("button");
        if (button && !button.disabled) closeMenus();
      }}
      onKeyDown={event=>{
        if (event.key!=="Escape") return;
        const details = (event.target as Element).closest("details[open]");
        if (!details) return;
        event.preventDefault();
        closeMenus();
        details.querySelector("summary")?.focus();
      }}>
      <details onToggle={e=>{if(e.currentTarget.open)closeMenus(e.currentTarget);}}>
        <summary>项目</summary>
        <div className="menu-commands">
          <button type="button" disabled={!snapshot} onClick={()=>press("add-project")}>打开项目</button>
          <button type="button" disabled={!project||!knowledgeUiLocal}
            onClick={()=>window.dispatchEvent(new CustomEvent("ff:project-settings",{detail:project}))}>项目设置</button>
          <button type="button" disabled={!project} onClick={()=>project&&navigation.removeProject(project)}>移除项目</button>
        </div>
      </details>
      <details onToggle={e=>{if(e.currentTarget.open)closeMenus(e.currentTarget);}}>
        <summary>查看</summary>
        <div className="menu-commands">
          <div id="workspace-mode-switch" role="group" aria-label="工作区模式">
            <button type="button" data-mode="agent" aria-pressed="true">协作工作区</button>
            <button type="button" data-mode="code" aria-pressed="false">代码模式</button>
          </div>
          <button id="menu-toggle-inspector" type="button" onClick={()=>press("toggle-inspector")}>切换右侧文件栏</button>
          <button type="button" disabled={!project} onClick={()=>showInspector("files")}>文件</button>
          <button type="button" disabled={!project} onClick={()=>showInspector("changes")}>Git 变更</button>
          <button type="button" disabled={!project} onClick={()=>showInspector("docs")}>协作文档</button>
          <button type="button" disabled={!project||!knowledgeUiLocal}
            onClick={()=>window.dispatchEvent(new Event("ff:open-knowledge"))}>知识库</button>
        </div>
      </details>
      <details onToggle={e=>{if(e.currentTarget.open)closeMenus(e.currentTarget);}}>
        <summary>工具</summary>
        <div className="menu-commands">
          <button type="button" onClick={()=>press("open-settings")}>设置</button>
          <button type="button" id="open-engine-settings">CLI 引擎与环境检测</button>
          <TeamProposalControl />
          <ResourceManager />
        </div>
      </details>
    </nav>
    <TopicNavigation />
    <button id="toggle-inspector" className="icon-button" type="button" aria-controls="inspector-shell"
      aria-label="切换右侧文件栏" aria-keyshortcuts="Control+Alt+B" title="切换右侧文件栏（Ctrl+Alt+B）"><WorkspaceIcon name="panel"/></button>
  </header>;
}

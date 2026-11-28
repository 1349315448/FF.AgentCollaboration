import { useEffect, useRef, useState } from "react";
import { ResourceManager } from "../dialogs/ResourceManager";
import { TeamProposalControl } from "../dialogs/TeamProposalDialog";
import { layout, useWorkbench } from "../../store/workbenchStore";
import { navigation } from "../../services/navigation";
import { knowledgeUiLocal } from "../../services/knowledge";
import { openGroupCollaboration } from "../../services/permanentGroups";
import { TopicNavigation } from "./TopicNavigation";

function press(id: string) { document.getElementById(id)?.click(); }

function showInspector(section: "files" | "docs" | "changes") {
  if (document.getElementById("inspector-shell")?.hidden) press("toggle-inspector");
  press("inspector-tab-" + section);
}

export function TopBar() {
  const {snapshot, topic, rosters} = useWorkbench();
  const menu = useRef<HTMLElement>(null);
  const [agentId,setAgentId] = useState<string|null>(null);
  const project = topic?.project ?? snapshot?.project;
  const active = topic?.status === "active";
  const agent = rosters.get(topic?.id ?? "")?.members.find(view=>view.agent.id===agentId)?.agent;

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
        <summary>小组</summary>
        <div className="menu-commands">
          <button type="button" disabled={!project} onClick={()=>project&&navigation.addTopic(project)}>新建小组</button>
          <button type="button" disabled={!topic} onClick={()=>topic&&navigation.editTopic(topic.id)}>编辑名称与目标</button>
          <button type="button" disabled={!active} onClick={()=>openGroupCollaboration(undefined,"local")}>小组合作</button>
          <TeamProposalControl />
          <button type="button" disabled={!topic} onClick={()=>topic&&navigation.archiveTopic(topic.id)}>{active?"归档小组":"重新打开小组"}</button>
        </div>
      </details>
      <details onToggle={e=>{
        if (!e.currentTarget.open) return;
        closeMenus(e.currentTarget);
        setAgentId((layout?.workspaceMode==="code" ? layout.codeActiveWindowId : layout?.activeWindowId) ?? null);
      }}>
        <summary>Agent</summary>
        <div className="menu-commands">
          <button type="button" id="new-agent" disabled={!active} onClick={()=>navigation.addAgent()}>添加 Agent</button>
          <button type="button" disabled={!agent} onClick={()=>agent&&navigation.powerAgent(agent.id)}>{agent?.online?"停止终端":"启动终端"}</button>
          <button type="button" disabled={!agent} onClick={()=>agent&&navigation.openAgentSessions(agent.id)}>会话记录</button>
          <button type="button" disabled={!agent} onClick={()=>agent&&navigation.renameAgent(agent.id)}>Agent 设置</button>
        </div>
      </details>
      <details onToggle={e=>{if(e.currentTarget.open)closeMenus(e.currentTarget);}}>
        <summary>查看</summary>
        <div className="menu-commands">
          <div id="workspace-mode-switch" role="group" aria-label="工作区模式">
            <button type="button" data-mode="agent" aria-pressed="true">协作工作区</button>
            <button type="button" data-mode="code" aria-pressed="false">代码模式</button>
          </div>
          <button id="toggle-inspector" type="button" aria-controls="inspector-shell" aria-expanded="false">切换右侧面板</button>
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
          <ResourceManager />
        </div>
      </details>
    </nav>
    <TopicNavigation />
  </header>;
}

import { TerminalPowerButton } from "./TerminalPowerButton";
import { usePane } from "./panePresentation";
import { PeerBadge } from "../federation/Federation";

export function PaneHeader() {
  const pane = usePane();
  return (
    <header className="pane-header">
      <div className="pane-title-group">
        <button className="text-button toggle-collapse" type="button" aria-expanded="true" title="缩小到顶部">⤡ 缩小</button>
        <TerminalPowerButton />
        <span className="nav-dot agent-light" title={pane?.status} aria-hidden="true"></span>
        <button type="button" className="tile-name agent-name rename-agent" aria-label="修改 Agent 名称"
          title={pane && "修改 @" + pane.name + " 的名称 · " + pane.engine + " · " + pane.project}>{pane && "@" + pane.name}</button>
        <span className="tile-engine" title={pane?.engine}>{pane?.engine === "GitHub Copilot" ? "Copilot" : pane?.engine}</span>
        <span className="primary-mark" hidden>主对话</span><span className="terminal-status status-label">{pane?.status}</span>
        <small className="approval-current" title="显示工作台为本次启动设置的审批权限；在 CLI 内手动修改后，以原生终端为准。">当前模式：引擎默认</small>
      </div>
      {pane && <PeerBadge id={pane.id}/>}
      <small className="mcp-restart" hidden title="MCP 配置已更新，重启终端生效">MCP 待重启</small>
      <button className="text-button pane-quota" type="button" aria-label="查看并刷新剩余额度"><span className="quota-value">额度</span><small className="quota-reset" hidden></small></button>
    </header>
  );
}

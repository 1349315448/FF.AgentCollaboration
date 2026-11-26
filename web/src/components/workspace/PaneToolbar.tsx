import { usePane } from "./panePresentation";

export function PaneToolbar() {
  const online = usePane()?.online;
  return (
    <footer className="terminal-toolbar">
      <div className="terminal-actions" role="group" aria-label="终端操作">
        <button className="text-button insert-agent" disabled={!online} type="button" aria-label="指定 Agent" title="提及 Agent（Alt+Shift+A）">@ <span className="action-label">Agent</span> <small className="mention-key">(Alt+Shift+A)</small></button>
        <button className="text-button insert-file" disabled={!online} type="button" aria-label="选择文件" title="插入文件（Alt+Shift+F）"><span className="folder-icon" aria-hidden="true"></span><span className="action-label">文件</span></button>
        <button className="text-button shortcuts-toggle" disabled={!online} type="button" aria-label="快捷指令" title="快捷指令">指令 ▾</button>
        <div className="shortcut-menu" popover="auto" aria-label="快捷指令"></div>
        <button className="text-button pane-approval" type="button" aria-label="切换审批" title="选择审批权限并启动或重启当前终端">切换审批</button>
        <div className="receive-controls">
          <label className="auto-receive"><input type="checkbox" role="switch" />自动接收</label>
          <select className="receive-mode" aria-label="收件方式" title="立即接收会立即开放会话收件。排队接收在 Agent 待命时开放。两种方式都保留输入草稿。">
            <option value="queued">排队接收</option>
            <option value="immediate">立即接收</option>
          </select>
        </div>
        <button className="text-button pane-sessions" type="button">会话记录</button>
        <button className="text-button set-primary" type="button">设为主对话</button>
        <button className="text-button pane-more" type="button" aria-label="更多窗格操作">更多 ▾</button>
        <div className="pane-more-menu" popover="auto" aria-label="更多窗格操作">
          <button className="text-button close-pane" type="button">移除 Agent</button>
        </div>
      </div>
    </footer>
  );
}

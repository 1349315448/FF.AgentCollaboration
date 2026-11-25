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
        <label className="auto-receive" title="在原生 CLI 的空闲输入行自动接收"><input type="checkbox" role="switch" />自动接收</label>
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

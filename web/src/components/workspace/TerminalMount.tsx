import { TerminalPowerButton } from "./TerminalPowerButton";
import { usePane } from "./panePresentation";

export function TerminalMount() {
  const pane = usePane();
  const starting = pane?.powerAction === "start";
  return <div className="terminal-surface">
    <div className="terminal-viewport" aria-label={pane && pane.name + " 原生终端"} />
    <div className="terminal-overlay" hidden={!pane || (pane.online && !starting)}>
      <strong className="offline-name">@{pane?.name}</strong>
      <div className="terminal-launch">
        <TerminalPowerButton launcher />
        <small className="exit-status" role="status" hidden={!starting && !pane?.exitText}>{starting ? "正在启动…" : pane?.exitText}</small>
      </div>
    </div>
  </div>;
}

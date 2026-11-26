import { TerminalPowerButton } from "./TerminalPowerButton";
import { usePane } from "./panePresentation";

export function TerminalMount() {
  const pane = usePane();
  const starting = pane?.powerAction === "start";
  return <div className="terminal-surface" tabIndex={-1} role="region" aria-label="终端历史">
    <div className="terminal-viewport" aria-label={pane && pane.name + " 原生终端"} />
    <div className="terminal-paging" hidden={!pane?.online} role="group" aria-label="终端历史翻页">
      <button type="button" className="reading-toggle" aria-pressed="false" title="F6 切换阅读与输入">阅读</button>
      <button type="button" data-page="-1" aria-label="向上翻页" title="向上翻页">↑</button>
      <button type="button" data-page="1" aria-label="向下翻页" title="向下翻页">↓</button>
    </div>
    <div className="terminal-overlay" hidden={!pane || (pane.online && !starting)}>
      <strong className="offline-name">@{pane?.name}</strong>
      <div className="terminal-launch">
        <TerminalPowerButton launcher />
        <small className="exit-status" role="status" hidden={!starting && !pane?.exitText}>{starting ? "正在启动…" : pane?.exitText}</small>
      </div>
    </div>
  </div>;
}

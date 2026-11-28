import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { TerminalPowerButton } from "./TerminalPowerButton";
import { usePane } from "./panePresentation";
import { PeerBadge } from "../federation/Federation";
import { LeaderFlag } from "../layout/LeaderFlag";
import { TopicLeaderButton } from "../dialogs/TopicLeaderDialog";

export function PaneHeader() {
  const pane = usePane();
  const headerRef = useRef<HTMLElement>(null);
  const titleRef = useRef<HTMLDivElement>(null);
  const moreRef = useRef<HTMLButtonElement>(null);
  const menuRef = useRef<HTMLDivElement>(null);
  const expandedMinWidth = useRef(0);
  const [compact, setCompact] = useState(false);

  useEffect(() => {
    const button = moreRef.current;
    const menu = menuRef.current;
    if (button && menu) (button as HTMLButtonElement & { popoverTargetElement: HTMLElement | null }).popoverTargetElement = menu;
  }, [compact]);

  useLayoutEffect(() => {
    const header = headerRef.current;
    if (!header) return;

    function measure() {
      const el = headerRef.current;
      const title = titleRef.current;
      if (!el || !title) return;
      const host = el.closest(".pane") as HTMLElement | null;
      if (host?.dataset.collapsed === "true") {
        if (compact) setCompact(false);
        return;
      }
      const width = el.clientWidth;
      if (!width) return;
      if (!compact) {
        const titleOverflow = title.scrollWidth - title.clientWidth;
        const headerOverflow = el.scrollWidth - width;
        if (titleOverflow > 1 || headerOverflow > 1) {
          expandedMinWidth.current = width + Math.max(titleOverflow, headerOverflow);
          setCompact(true);
        }
      } else if (expandedMinWidth.current > 0 && width >= expandedMinWidth.current + 16) {
        if (menuRef.current?.matches(":popover-open")) menuRef.current.hidePopover();
        setCompact(false);
      }
    }

    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(header);
    if (titleRef.current) observer.observe(titleRef.current);
    return () => observer.disconnect();
  }, [compact, pane?.name, pane?.isLeader, pane?.status, pane?.engine]);

  function onMenuClick(event: React.MouseEvent<HTMLDivElement>) {
    const target = event.target as HTMLElement | null;
    if (target?.closest(".pane-leader-action, .fed-badge") && menuRef.current?.matches(":popover-open")) {
      menuRef.current.hidePopover();
    }
  }

  return (
    <header className="pane-header" ref={headerRef} data-compact={compact ? "true" : undefined}>
      <div className="pane-title-group" ref={titleRef}>
        <button className="text-button toggle-collapse" type="button" aria-expanded="true" title="缩小到顶部">⤡ 缩小</button>
        <TerminalPowerButton />
        <span className="nav-dot agent-light" title={pane?.status} aria-hidden="true"></span>
        <span className="pane-name">
          <button type="button" className="tile-name agent-name terminal-title">{pane && "@" + pane.name}</button>
          {pane?.isLeader && <LeaderFlag />}
        </span>
        {pane && !compact && <TopicLeaderButton agentId={pane.id} />}
        <span className="tile-engine" title={pane?.engine}>{pane?.engine === "GitHub Copilot" ? "Copilot" : pane?.engine}</span>
        <span className="primary-mark" hidden>主对话</span><span className="terminal-status status-label">{pane?.status}</span>
        <small className="approval-current" title="显示工作台为本次启动设置的审批权限；在 CLI 内手动修改后，以原生终端为准。">当前模式：引擎默认</small>
      </div>
      {pane && !compact && <PeerBadge id={pane.id}/>}
      <small className="mcp-restart" hidden title="MCP 配置已更新，重启终端生效">MCP 待重启</small>
      <button className="text-button pane-quota" type="button" aria-label="查看并刷新剩余额度"><span className="quota-value">额度</span><small className="quota-reset" hidden></small></button>
      {pane && compact && <>
        <button className="text-button pane-header-more" ref={moreRef} type="button" aria-label="更多头部操作" title="更多操作（组长 / 外部协作）">更多 ▾</button>
        <div className="pane-header-more-menu" ref={menuRef} popover="auto" aria-label="更多头部操作" onClick={onMenuClick}>
          <TopicLeaderButton agentId={pane.id} />
          <PeerBadge id={pane.id} />
        </div>
      </>}
    </header>
  );
}

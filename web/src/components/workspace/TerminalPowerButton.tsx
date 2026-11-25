import { usePane } from "./panePresentation";

export function TerminalPowerButton({ launcher = false }: { launcher?: boolean }) {
  const pane = usePane();
  const online = !!pane?.online && !launcher;
  const starting = pane?.powerAction === "start";
  const action = starting ? "正在启动" : online ? "关闭终端" : "打开终端";
  return (
    <button type="button" className={"terminal-power " + (launcher ? "start-pane" : "pane-power")}
      title={action} aria-label={launcher ? action : (pane ? "@" + pane.name + " 的终端电源" : "终端电源")}
      data-center-launch={!launcher && (!online || starting) || undefined} aria-busy={starting || undefined}
      aria-pressed={launcher ? undefined : online} disabled={!pane || !!pane.powerAction}>
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" aria-hidden="true">
        <path d="M12 3v9m-5-7a8 8 0 1 0 10 0" />
      </svg>
    </button>
  );
}

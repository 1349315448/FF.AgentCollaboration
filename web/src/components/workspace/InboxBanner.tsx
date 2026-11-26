import { usePane } from "./panePresentation";

export function InboxBanner() {
  const pane = usePane();
  const all = (pane?.ignoreCount ?? 0) > 1;
  return <div className="inbox" aria-live="polite" hidden={!pane?.inboxState} data-state={pane?.inboxState}>
    <span className="inbox-caption">{pane?.caption}</span>
    <button className="text-button next-inbox" type="button" hidden={(pane?.inboxCount ?? 0) < 2}>下一条</button>
    <button className="text-button preview-inbox" type="button">预览</button>
    <button className={all ? "button compact ack-inbox ack-all-inbox" : "button compact ack-inbox"} type="button"
      title={all ? "忽略此窗所有待收和待核对消息，保留日志" : "忽略此消息，保留日志"} hidden={!all && !pane?.canIgnore}>{all ? "全部忽略" : "忽略"}</button>
    <button className="button compact write-inbox" type="button" hidden={pane?.inboxState !== "pending"} disabled={!pane?.online}
      title={pane?.online ? "向当前会话投递，保留正在编辑的草稿" : "终端已退出"}>立即投递</button>
  </div>;
}

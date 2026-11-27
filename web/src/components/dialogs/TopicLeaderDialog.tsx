import { useEffect, useRef, useState } from "react";
import { request } from "../../services/api";
import { useWorkbench } from "../../store/workbenchStore";
import { LeaderFlag } from "../layout/LeaderFlag";
import { WorkspaceIcon } from "../layout/WorkspaceIcon";

interface LeaderChange {
  topicId: string;
  windowId: string | null;
  expectedLeaderWindowId: string;
  name: string;
  previousName?: string;
}
const changeEvent = "ff:confirm-topic-leader";

export function TopicLeaderButton({ agentId, inSettings = false }: { agentId: string; inSettings?: boolean }) {
  const { snapshot } = useWorkbench();
  const agent = snapshot?.windows.find(view => view.agent.id === agentId)?.agent;
  const topic = snapshot?.topics.find(topic => topic.id === agent?.topicId);
  if (!agent || topic?.status !== "active") return null;
  const previousId = topic.leaderWindowId ?? "";
  const current = previousId === agent.id;
  const previous = snapshot?.windows.find(view => view.agent.id === previousId)?.agent;
  const local = location.hostname === "127.0.0.1";

  function confirm() {
    if (!agent || !topic) return;
    const detail: LeaderChange = {
      topicId: topic.id, windowId: current ? null : agent.id, name: agent.name,
      expectedLeaderWindowId: previousId, previousName: previous?.name
    };
    window.dispatchEvent(new CustomEvent(changeEvent, { detail }));
  }

  const label = current ? "取消组长" : "设为组长";
  const button = <button type="button" className={inSettings ? "button" : "text-button pane-leader-action"}
    disabled={!local} aria-label={label} title={local ? label : "请在工作台主机上设置组长"} onClick={confirm}>
    {inSettings ? current && <LeaderFlag /> : <WorkspaceIcon name="flag" />}<span>{label}</span>
  </button>;
  return inSettings ? <div className="topic-leader-setting"><span>本话题组长</span>{button}</div> : button;
}

export function TopicLeaderDialog() {
  const dialog = useRef<HTMLDialogElement>(null);
  const [change, setChange] = useState<LeaderChange>();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");

  useEffect(() => {
    const open = (event: Event) => {
      if (dialog.current?.open) return;
      setChange((event as CustomEvent<LeaderChange>).detail);
      setError("");
    };
    window.addEventListener(changeEvent, open);
    return () => window.removeEventListener(changeEvent, open);
  }, []);
  useEffect(() => {
    if (change && !dialog.current?.open) dialog.current?.showModal();
  }, [change]);

  const title = !change?.windowId ? "取消话题组长" : change.previousName ? "移交话题组长" : "设置话题组长";
  const description = !change?.windowId ? `取消 @${change?.name} 的组长身份后，本话题将恢复为无组长状态。` :
    change.previousName ? `将组长身份与权限从 @${change.previousName} 移交给 @${change.name}。原组长会自动卸任，可以在对话中通知对方交接进度。` :
      `将 @${change.name} 设为本话题组长，授予总计划 PLAN.md 的写权限与团队组建权限。`;
  return <dialog ref={dialog} id="topic-leader-dialog" aria-labelledby="topic-leader-title" onClose={() => setChange(undefined)}
    onCancel={event => { if (busy) event.preventDefault(); }}>
    <form onSubmit={async event => {
      event.preventDefault();
      if (!change || busy) return;
      setBusy(true);
      setError("");
      try {
        await request(`/topics/${change.topicId}/leader`, {
          method: "PUT",
          body: { windowId: change.windowId, expectedLeaderWindowId: change.expectedLeaderWindowId }
        });
        dialog.current?.close();
      } catch (failure) {
        setError((failure as Error).message);
      } finally {
        setBusy(false);
      }
    }}>
      <header className="dialog-header"><h2 id="topic-leader-title">{title}</h2></header>
      <div className="dialog-body">
        {change && <p>{description}</p>}
        {error && <p className="form-error" role="alert">{error}</p>}
      </div>
      <footer className="dialog-footer">
        <button type="button" className="button" disabled={busy} onClick={() => dialog.current?.close()}>取消</button>
        <button type="submit" className="button primary" disabled={busy || !!error}>{busy ? "正在保存…" : "确认"}</button>
      </footer>
    </form>
  </dialog>;
}

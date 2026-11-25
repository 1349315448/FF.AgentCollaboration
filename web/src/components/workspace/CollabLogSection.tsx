import { useRef, useLayoutEffect } from "react";
import { useWorkbench, type TimelineEntry } from "../../store/workbenchStore";

const receipts: Record<string,string> = {pending:"待接收", writing:"写入中", delivered:"已送达", delivery_uncertain:"待核对"};
const empty: TimelineEntry[] = [];
function timestamp(value?: string) {
  if (!value) return "—";
  const d = new Date(value);
  if (Number.isNaN(d.getTime())) return "—";
  const two = (n:number) => String(n).padStart(2,"0");
  return d.getFullYear() + "-" + two(d.getMonth()+1) + "-" + two(d.getDate()) + " " + two(d.getHours()) + ":" + two(d.getMinutes()) + ":" + two(d.getSeconds());
}
function LogRow({entry}: {entry: TimelineEntry}) {
  const stamp = timestamp(entry.createdAt);
  const route = entry.from + " → " + entry.to;
  const status = entry.acknowledged ? "已忽略" : receipts[entry.state] || entry.state;
  return <button type="button" className="timeline-entry" data-message={entry.id} data-state={entry.acknowledged ? "ignored" : entry.state}
    title={[stamp,route,entry.summary,status].join(" · ")}>
    <span className="log-time" title={entry.createdAt ? stamp : "历史时间未记录"}>{stamp}</span>
    <span className="log-route">{route}</span><span className="log-summary">{entry.summary}</span><span className="log-state">{status}</span>
  </button>;
}
export function CollabLogSection() {
  const { snapshot, selectedTopicId } = useWorkbench();
  const list = useRef<HTMLDivElement>(null);
  const entries = selectedTopicId && snapshot?.timelineTopic === selectedTopicId ? snapshot.timeline : empty;
  const latest = entries[0];
  useLayoutEffect(() => { if (list.current) list.current.scrollTop = 0; }, [selectedTopicId, latest?.id]);
  return <section id="collab-log" aria-label="协作日志" className={latest ? "has-messages" : undefined} hidden={!selectedTopicId}>
    <div id="log-divider" role="separator" tabIndex={0} aria-orientation="horizontal" aria-label="调整日志高度" aria-controls="topic-timeline" aria-valuemin={1} aria-valuemax={15} />
    <button type="button" id="log-toggle" aria-controls="topic-timeline" aria-expanded="true">
      <strong>日志</strong><span id="log-latest">{latest ? latest.from + " → " + latest.to + " · " + latest.summary : "暂无记录"}</span><span id="log-action">▾</span>
    </button>
    <div ref={list} id="topic-timeline" aria-label="交接消息">{entries.map(entry => <LogRow key={entry.id} entry={entry} />)}</div>
  </section>;
}

import { useRef, useLayoutEffect, useState, type ComponentProps } from "react";
import { WorkspaceIcon } from "../layout/WorkspaceIcon";
import { MailApprovals, viewApplication, type PeerMail } from "../federation/Federation";
import { useWorkbench, type TimelineEntry } from "../../store/workbenchStore";

const receipts: Record<string,string> = {pending:"待接收", writing:"写入中", delivered:"已送达", delivery_uncertain:"待核对",ignored:"已忽略",accepted:"对端已接收",sent:"已发出",sending:"发送中",superseded:"已覆盖",cancelled:"已撤回",blocked:"已拦截"};
const workStates:Record<string,string> = {queued:"排队中",received:"已接收",working:"正在处理",need_info:"待补充",completed:"已完成",replied:"已回复"};
function WorkState({state}:{state?:string|null}) {
  const label=state && workStates[state];
  return <span className="log-work-state" data-work={state??undefined} title={label?"协作状态 · "+label:"没有任务进度回执"}>{label || "—"}</span>;
}
const mailIcons: Record<string, ComponentProps<typeof WorkspaceIcon>["name"]> = {
  application: "mailRequest", incoming: "mailIncoming", outgoing: "mailOutgoing",
  "reply-in": "mailReply", "reply-out": "mailReply", permission:"mailNotice", blocked: "mailNotice", audit: "mailNotice"
};
const empty: TimelineEntry[] = [];
function timestamp(value?: string) {
  if (!value) return "—";
  const d = new Date(value);
  if (Number.isNaN(d.getTime())) return "—";
  const two = (n:number) => String(n).padStart(2,"0");
  return d.getFullYear() + "-" + two(d.getMonth()+1) + "-" + two(d.getDate()) + " " + two(d.getHours()) + ":" + two(d.getMinutes()) + ":" + two(d.getSeconds());
}
function openDocument(topic:string,id:string) {window.dispatchEvent(new CustomEvent("ff:open-document",{detail:{topic,id}}));}
function LogRow({entry,topic}: {entry: TimelineEntry;topic:string}) {
  const stamp = timestamp(entry.createdAt);
  const route = entry.from + " → " + entry.to;
  const status = entry.acknowledged ? "已忽略" : receipts[entry.state] || entry.state;
  const open=()=>openDocument(topic,entry.hasDocument?entry.id:"message:"+entry.id);
  const row=<button onClick={open} type="button" className="timeline-entry" data-message={entry.id} data-state={entry.acknowledged ? "ignored" : entry.state}
    title={[stamp,route,entry.summary,status].join(" · ")}>
    <span className="log-time" title={entry.createdAt ? stamp : "历史时间未记录"}><WorkspaceIcon name="envelope"/>{stamp}</span>
    <span className="log-route">{route}</span><span className="log-summary">{entry.summary}</span><span className="log-state">{status}</span>
    <WorkState state={entry.workState}/>
  </button>;
  return <div className="log-document-row">{row}{entry.hasDocument?<button type="button" aria-label="预览交接文档" title="预览交接文档" onClick={open}><WorkspaceIcon name="docs"/></button>:<span aria-hidden="true"/>}</div>;
}
export function CollabLogSection() {
  const { snapshot, selectedTopicId } = useWorkbench();
  const list = useRef<HTMLDivElement>(null);
  const entries = selectedTopicId && snapshot?.timelineTopic === selectedTopicId ? snapshot.timeline : empty;
  const latest = entries[0];
  const [filter,setFilter]=useState("全部"),[seen,setSeen]=useState<{topic:string;seq:number}>();
  const fed=snapshot?.federation;
  const mail=(fed?.mail??[]).filter(m=>m.topicId===selectedTopicId);
  const pending=(fed?.grants??[]).filter(g=>g.status==="pending" && snapshot?.windows.some(w=>w.agent.topicId===selectedTopicId && w.agent.id===g.windowId));
  const key="ff-mail-seen-"+selectedTopicId;
  const checkpoint=seen?.topic===key?seen.seq:Number(localStorage.getItem(key)||0);
  const isIncoming=(m:PeerMail)=>["incoming","reply-in","audit","blocked","permission"].includes(m.kind);
  const unread=mail.filter(m=>m.seq>checkpoint && isIncoming(m)).length;
  function see(){const seq=mail[0]?.seq||0;localStorage.setItem(key,String(seq));setSeen({topic:key,seq});}
  const names:Record<string,string>={application:"接入申请",incoming:"外部来信",outgoing:"发往外部","reply-in":"外部回信","reply-out":"回信已发",permission:"协作权限通知",blocked:"已拦截",audit:"变更告警"};
  function peerRow(m:PeerMail){
    const icon=mailIcons[m.kind] ?? "envelope";
    const application=m.kind==="application" && pending.some(g=>g.id===m.source);
    const open=()=>application?viewApplication(m.source):openDocument(m.topicId,"peer:"+m.seq);
    return <div className={"log-document-row"+(application?" pending-application":"")} key={"peer"+m.seq}><button type="button" className="timeline-entry fed-mail-row" data-kind={m.kind} onClick={open}>
      <span className="log-time"><WorkspaceIcon name={icon}/>{timestamp(m.createdAt)}</span>
      <span className="log-route" title={names[m.kind]}>{m.label}</span><span title={m.text}>{m.text}</span>
      <span className="log-state" title={m.deliveryState ?? names[m.kind]}>{m.deliveryState ? receipts[m.deliveryState] ?? "投递失败" : "—"}</span>
      <WorkState state={m.workState}/>
    </button>{application?<button type="button" className="text-button" onClick={open}>查看</button>:
      m.hasDocument?<button aria-label="预览交接文档" onClick={open}><WorkspaceIcon name="docs"/></button>:<span aria-hidden="true"/>}</div>;
  }
  const localRows=filter==="全部"?entries.filter(e=>e.source!=="peer-mailbox"&&!fed?.remotes.some(r=>r.id===e.source))
    .map(e=>({date:e.createdAt??"",row:<LogRow key={e.id} entry={e} topic={selectedTopicId!}/>})):[];
  const rows=[...localRows,...mail.filter(m=>filter==="全部" || filter==="外部来信" && isIncoming(m))
    .map(m=>({date:m.createdAt,row:peerRow(m)}))].sort((a,b)=>b.date.localeCompare(a.date));
  const uncertain = entries.filter(entry=>entry.state==="delivery_uncertain"&&!entry.acknowledged).length;
  useLayoutEffect(() => { if (list.current) list.current.scrollTop = 0; }, [selectedTopicId, latest?.id,mail[0]?.seq]);
  return <section id="collab-log" aria-label="协作信箱" className={latest || mail.length ? "has-messages" : undefined} hidden={!snapshot?.project}>
    <div id="log-divider" role="separator" tabIndex={0} aria-orientation="horizontal" aria-label="调整日志高度" aria-controls="topic-timeline" aria-valuemin={1} aria-valuemax={15} />
    <button type="button" id="log-toggle" aria-controls="topic-timeline" aria-expanded="true">
      <span id="peer-mailbox-icon"><WorkspaceIcon name="mailbox"/></span><strong>协作信箱</strong>
      {unread>0 && <span>{unread} 未读</span>}{pending.length>0 && <span>{pending.length} 待审</span>}
      <span id="log-latest">{mail[0] && (!latest?.createdAt || mail[0].createdAt>latest.createdAt)?mail[0].text:latest ? latest.from + " → " + latest.to + " · " + latest.summary : "暂无记录"}</span>
      {uncertain>0 && <span className="log-state" title="接收终端尚未确认提交，可展开日志核对">{uncertain} 条待核对</span>}
      {latest?.createdAt && <time className="log-time" dateTime={latest.createdAt}>{timestamp(latest.createdAt).slice(-8)}</time>}<span id="log-action">▾</span>
    </button>
    <div className="fed-filters" aria-label="筛选信箱">{["全部","外部来信","待审申请"].map(value=>
      <button className="text-button" key={value} aria-pressed={filter===value} onClick={()=>{setFilter(value);see();}}>{value}</button>)}
      {filter!=="待审申请" && <span className="mail-column-labels"><span>投递状态</span><span>协作状态</span></span>}</div>
    <div ref={list} id="topic-timeline" aria-label="交接消息">
      {filter==="待审申请"?<MailApprovals grants={pending}/>:rows.map(r=>r.row)}
    </div>
  </section>;
}

import { byId } from "./dom.js";
import { narrow } from "./layout.js";
import { panes } from "../services/terminalRegistry";
import { selectedTopicId, snapshot } from "../store/workbenchStore";
import { projectTab } from "./topics.js";

const breatheAnimations = new Set(["agent-breathe", "idle-breathe", "waiting-breathe"]);
// 话题列表重绘或切换布局时，新出现的灯也从当前页面的同一呼吸相位开始。
document.addEventListener("animationstart", event => {
  if (!breatheAnimations.has(event.animationName)) return;
  const pulse = event.target.getAnimations().find(animation => animation.animationName === event.animationName);
  if (pulse && pulse.startTime !== 0) pulse.startTime = 0;
});
function attachLogControls() {
  const panel = byId("collab-log"), list = byId("topic-timeline");
  const toggle = byId("log-toggle"), divider = byId("log-divider");
  function collapse(value) {
    list.hidden = divider.hidden = value;
    toggle.setAttribute("aria-expanded", String(!value));
    byId("log-action").textContent = value ? "▴" : "▾";
    list.scrollTop = 0;
  }
  collapse(narrow.matches);
  narrow.addEventListener("change", () => collapse(narrow.matches));
  toggle.onclick = () => collapse(!list.hidden);
  // 展开、拖高或窗格重排后继续保持顶部最新记录可见，仅滚动日志自身。
  new ResizeObserver(() => { list.scrollTop = 0; }).observe(list);

  const rowHeight = parseFloat(getComputedStyle(panel).getPropertyValue("--log-row"));
  let rows = 5, startY, startRows;
  function resize(value) {
    rows = Math.min(15, Math.max(1, Math.round(value)));
    panel.style.setProperty("--log-rows", rows);
    divider.setAttribute("aria-valuenow", rows);
  }
  resize(rows);
  divider.onpointerdown = event => {
    if (!event.isPrimary || event.button !== 0) return;
    startY = event.clientY;
    startRows = list.clientHeight / rowHeight;
    divider.setPointerCapture(event.pointerId);
  };
  divider.onpointermove = event => {
    if (divider.hasPointerCapture(event.pointerId)) resize(startRows + (startY - event.clientY) / rowHeight);
  };
  divider.onkeydown = event => {
    if (event.key !== "ArrowUp" && event.key !== "ArrowDown") return;
    event.preventDefault();
    resize(rows + (event.key === "ArrowUp" ? 1 : -1));
  };
}

function visibleIn(element, container) {
  if (!element?.getClientRects().length || !container?.getClientRects().length) return false;
  const rect=element.getBoundingClientRect(), box=container.getBoundingClientRect();
  return rect.width>0 && rect.left>=box.left-1 && rect.right<=box.right+1 && rect.top>=box.top-1 && rect.bottom<=box.bottom+1;
}

function messageAnchor(id, preferTag = false) {
  if (id==="peer-mailbox") return byId("peer-mailbox-icon");
  if (!id) return null;
  const pane=panes.get(id);
  const remote=snapshot.federation?.remotes.find(r=>r.id===id);
  if (!pane && !remote) return null;
  const topicId=pane?.agent.topicId ?? remote.topicId;
  if (pane && !preferTag && topicId===selectedTopicId && pane.element.dataset.collapsed!=="true" && pane.element.getClientRects().length)
    return pane.element.querySelector(".agent-name") || pane.element;
  const attr=remote?'[data-remote-id="'+CSS.escape(id)+'"]':'[data-agent="'+CSS.escape(id)+'"]';
  const list=byId("topic-list"), rail=byId("project-tabs");
  const chip=list?.querySelector(attr);
  if (visibleIn(chip,list)) return chip;
  const member=rail.querySelector(attr);
  if (visibleIn(member,rail)) return member;
  const topic=rail.querySelector('.topic-choice[data-topic="'+CSS.escape(topicId)+'"]');
  if (visibleIn(topic,rail)) return topic;
  const owner=snapshot.topics.find(t=>t.id===topicId);
  const project=owner && projectTab(owner.project);
  if (visibleIn(project,rail)) return project;
  return topicId===selectedTopicId ? document.querySelector(".current-topic") : byId("project-collapse");
}

function handoffSvg(className, viewBox, drawing) {
  const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
  svg.setAttribute("class", className);
  if (viewBox) svg.setAttribute("viewBox", viewBox);
  svg.setAttribute("aria-hidden", "true");
  svg.innerHTML = drawing;
  return svg;
}

async function sweepRecipient(anchor) {
  const frame = anchor?.closest(".pane, .topic-agent-tag, .tree-agent, .topic-choice, .project-button, .current-topic, #project-collapse, .remote-capsule, .tree-remote, #peer-mailbox-icon");
  if (!frame?.getClientRects().length) return;
  // 没有 viewBox 的 SVG 使用宿主的实际像素，窗格缩放时圆角和线宽保持一致。
  const sweep = handoffSvg("receipt-sweep", null,
    '<rect class="receipt-outline" pathLength="100"/>' +
    '<rect class="receipt-highlight receipt-right" pathLength="100"/>' +
    '<rect class="receipt-highlight receipt-down" pathLength="100"/>');
  const radius = parseFloat(getComputedStyle(frame).borderTopLeftRadius);
  sweep.style.setProperty("--receipt-radius", Math.max(0, radius - 2) + "px");
  frame.append(sweep);
  try {
    await sweep.getAnimations()[0]?.finished;
  } catch (error) {
    if (error.name !== "AbortError") throw error;
  } finally {
    sweep.remove();
  }
}

async function flyMessage(entry, preferTag = false) {
  if (document.visibilityState !== "visible") return;
  const from = messageAnchor(entry.source, preferTag);
  const fromTab = !!from?.closest(".topic-agents, #project-tabs");
  let to = messageAnchor(entry.target, preferTag);
  if (!to?.getClientRects().length) return;
  if (!from?.getClientRects().length || from === to) return sweepRecipient(to);
  const source = from.getBoundingClientRect();
  const x = source.left + source.width / 2, y = source.top + source.height / 2;
  const envelope = handoffSvg("message-flight", "0 0 24 18",
    '<rect x="1" y="1" width="22" height="16" rx="3"/><path d="m2 3 10 7L22 3"/>');
  const hand = handoffSvg("message-flick-hand", "0 0 28 28",
    '<g><path fill="#1279bc" d="m2 19 5-6 3-4q1-2 3-1l2 1 6-5q2-2 3 0 1 1-1 3l-7 6 1 4-2 3q-2 2-5 2l-3 4z"/>' +
    '<path class="flick-thumb" fill="#38bdf8" d="M12 15q3-2 6-2l4-3q2-1 3 1t-2 3l-5 4-5 1"/>' +
    '<path fill="none" d="m9 12 2 2m1-5 2 2"/><path fill="#12426b" d="m1 20 3-3 7 7-3 3z"/></g>');
  const mailbox = handoffSvg("message-mailbox", "0 0 28 28",
    '<path fill="#164e63" d="M2 22V12a8 8 0 0 1 8-8h7a8 8 0 0 1 8 8v10z"/>' +
    '<path fill="#0e7490" d="M2 22V12a8 8 0 0 1 16 0v10z"/><path d="M5 24h18"/>' +
    '<rect class="mailbox-slot" x="4" y="12" width="12" height="4" rx="1.7"/>' +
    '<g class="mailbox-flag"><path d="M21 16V3"/><path fill="#f43f5e" d="M21 3h6l-1.5 2L27 7h-6z"/></g>');
  const slot = mailbox.querySelector(".mailbox-slot");
  const flag = mailbox.querySelector(".mailbox-flag");
  const glow = [{textShadow:"0 0 8px var(--accent)"},{textShadow:"none"}];
  const pulse = [{backgroundColor:"var(--wash)", boxShadow:"inset 0 0 0 1px var(--accent)"},
    {backgroundColor:"transparent", boxShadow:"inset 0 0 0 1px transparent"}];
  let tx, ty, direction, arcLength, approach, trackingTimer, sweep;
  function alignMailbox() {
    const anchor = messageAnchor(entry.target, preferTag);
    if (!anchor?.getClientRects().length) return;
    to = anchor;
    const bounds = to.getBoundingClientRect();
    const cx = bounds.left + bounds.width / 2, cy = bounds.top + bounds.height / 2;
    if (cx === tx && cy === ty) return;
    tx = cx; ty = cy;
    // 把真实投递口（28x28 viewBox 中 x=4..16, y=12..16，中心 10,14）摆到收件锚点，避免写入后强读回流。
    mailbox.style.left = (tx - 10) + "px";
    mailbox.style.top = (ty - 14) + "px";
    const sx = tx, sy = ty;
    const distance = Math.hypot(sx - x, sy - y);
    const lift = Math.max(fromTab ? 82 : 56, Math.min(140, distance * .28));
    direction = sx >= x ? 1 : -1;
    approach = Math.min(16, distance * .12);
    arcLength = Math.hypot(distance, lift * 1.15);
    const controlY = fromTab ? Math.min(innerHeight - 10, Math.max(y, sy) + lift) :
      Math.max(8, Math.min(y, sy) - lift);
    const curve = "M " + x + " " + y + " Q " + (x + sx) / 2 + " " + controlY + " " +
      (sx - direction * approach) + " " + sy + " L " + sx + " " + sy;
    envelope.style.offsetPath = "path('" + curve + "')";
  }
  const onViewportChange = () => alignMailbox();
  try {
    document.body.append(mailbox, envelope, hand);
    alignMailbox();
    if (arcLength == null) return;
    window.addEventListener("resize", onViewportChange, { passive: true });
    window.addEventListener("ff:layout-changed", onViewportChange);
    // 兜底低频校位（120ms），避免每帧 60Hz 同步布局回流，同时兼容非布局事件引起的位移。
    trackingTimer = setInterval(alignMailbox, 120);
    const sign = direction;
    if (sign < 0) hand.firstElementChild.setAttribute("transform", "translate(28 0) scale(-1 1)");
    hand.style.left = (x - (sign > 0 ? 30 : -2)) + "px";
    hand.style.top = (y - 4) + "px";
    hand.style.transformOrigin = sign > 0 ? "23px 6px" : "5px 6px";
    from.animate(glow, 280);
    from.closest(".topic-agent-tag, .tree-agent, .topic-choice, .project-button")?.animate(pulse, 320);
    const flick = "rotate(" + (18 * sign) + "deg) scale(1.06)";
    const grip = "rotate(0deg)", windup = "rotate(" + (-20 * sign) + "deg) scale(.96)";
    await hand.animate([
      {transform:grip}, {offset:.35,transform:grip},
      {offset:.78,transform:windup}, {transform:flick}
    ], {duration:760, easing:"ease-in-out", fill:"forwards"}).finished;
    hand.querySelector(".flick-thumb").animate([{transform:"rotate(0deg)"},{transform:"rotate(22deg)"}],
      {duration:170,fill:"forwards"});
    hand.animate([{transform:flick,opacity:1},{transform:"translateX(" + (3 * sign) + "px) " + flick,opacity:0}],
      {duration:240,fill:"forwards"});

    const turns = Math.max(1, Math.min(5, Math.round(arcLength / 300)));
    const degrees = direction * turns * 360;
    const duration = Math.max(1050, Math.min(1850, Math.round(720 + arcLength * .62)));
    mailbox.animate([{opacity:0},{opacity:1}], {delay:duration * .35, duration:150, fill:"both"});
    const level = "rotate(" + degrees + "deg)";
    await envelope.animate([
      {offsetDistance:"0%",transform:"rotate(0deg) scale(.95)",opacity:1},
      {offset:.85,offsetDistance:"85%",transform:"rotate(" + (.85 * degrees) + "deg) scale(.95)",opacity:1},
      {offset:.94,offsetDistance:(100 * (1 - approach / arcLength)) + "%",transform:level + " scale(.95)",opacity:1},
      {offsetDistance:"100%",transform:level + " scale(.42)",opacity:0}
    ], {duration,fill:"forwards"}).finished;

    alignMailbox();
    clearInterval(trackingTimer);
    window.removeEventListener("resize", onViewportChange);
    window.removeEventListener("ff:layout-changed", onViewportChange);
    sweep = sweepRecipient(to);
    to.animate(glow, 320);
    to.closest(".pane-header, .topic-agent-tag, .tree-agent, .topic-choice, .project-button")?.animate(pulse, 320);
    flag.animate([{transform:"rotate(-75deg)"},{transform:"rotate(0deg)"}], {duration:160,easing:"ease-out",fill:"forwards"});
    await mailbox.animate([{transform:"scale(1)",opacity:1},{offset:.16,transform:"scale(1.14,.90)"},
      {offset:.36,transform:"scale(.96,1.04)"},{offset:.76,transform:"scale(1)",opacity:1},
      {transform:"scale(1)",opacity:0}], {duration:360,easing:"ease-out",fill:"forwards"}).finished;
  } catch (error) {
    if (error.name !== "AbortError") throw error;
  } finally {
    clearInterval(trackingTimer);
    window.removeEventListener("resize", onViewportChange);
    window.removeEventListener("ff:layout-changed", onViewportChange);
    hand.remove(); envelope.remove(); mailbox.remove();
  }
  await sweep;
}

function receiptEffect(entry, delivered) {
  const target = messageAnchor(entry.target);
  if (!target?.getClientRects().length || document.visibilityState !== "visible") return;
  if (delivered) {
    const surface = target.closest(".pane") || target;
    surface.animate([{boxShadow:"inset 0 0 0 2px var(--accent)"},{boxShadow:"inset 0 0 0 2px transparent"}], 340);
    return;
  }
  flyMessage(entry);
  const badge = target.querySelector(".agent-mail-count, .tag-inbox:not([hidden])") || target;
  const frames = badge === target
    ? [{backgroundColor:"var(--wash)"},{backgroundColor:"transparent"}]
    : [{transform:"scale(1)"},{transform:"scale(1.18)"},{transform:"scale(1)"}];
  badge.animate(frames, 260);
}

// 只比较相邻快照。整页重载、切换话题及标为已读均不重播历史交接。
function animateMessages(previous, current) {
  if (!previous) return;
  if(previous.federation && current.federation) {
    const cursor=previous.federation.mail?.[0]?.seq||0;
    for(const event of current.federation.mail||[]) {
      if(event.seq<=cursor) break;
      if(["incoming","outgoing","reply-in","reply-out","permission"].includes(event.kind)) receiptEffect(event,false);
    }
  }
  const sameTopic = current.timelineTopic && previous.timelineTopic === current.timelineTopic;
  function messages(view) {
    const entries = new Map();
    if (sameTopic) for (const item of view.timeline) entries.set(item.id, item);
    for (const w of view.windows) for (const item of w.inbox) {
      const timeline = entries.get(item.id);
      const source = item.source || timeline?.source || view.windows.find(peer => peer.agent.name === item.from &&
        peer.agent.topicId === (item.fromTopic ? view.topics.find(t => t.name === item.fromTopic)?.id : w.agent.topicId))?.agent.id;
      entries.set(item.id, {...item, source});
    }
    return entries;
  }
  const before = messages(previous);
  const after = messages(current);
  // 最新投递在前；遇到上一帧已有的 ID 后，后面的旧记录可能只是清理日志后补入了这 30 条。
  const known = new Set((previous.recentHandoffs || []).map(item => item.id));
  for (const item of previous.recentHandoffs || []) if (!before.has(item.id)) before.set(item.id, item);
  let newPrefix = true;
  for (const item of current.recentHandoffs || []) {
    if (!previous.recentHandoffs) break;
    if (known.has(item.id)) newPrefix = false;
    if (!after.has(item.id) && !item.acknowledged && (newPrefix || before.has(item.id))) after.set(item.id, item);
  }
  for (const entry of after.values()) {
    if(entry.source==="peer-mailbox" || current.federation?.remotes?.some(r=>r.id===entry.source)) continue;
    const old = before.get(entry.id);
    if (!old) receiptEffect(entry, false);
    if (entry.state === "delivered" && old?.state !== "delivered") receiptEffect(entry, true);
  }
}

export { animateMessages, attachLogControls, flyMessage, messageAnchor };

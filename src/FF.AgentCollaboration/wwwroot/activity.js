"use strict";

const activityLabels = { offline: "离线", idle: "待命", working: "进行中", waiting: "需要处理", finished: "已结束" };
const breatheAnimations = new Set(["agent-breathe", "idle-breathe", "waiting-breathe"]);
function statusDot(extraClass) {
  const dot = node("span", extraClass ? "nav-dot " + extraClass : "nav-dot");
  dot.setAttribute("aria-hidden", "true");
  return dot;
}
// 话题列表重绘或切换布局时，新出现的灯也从当前页面的同一呼吸相位开始。
document.addEventListener("animationstart", event => {
  if (!breatheAnimations.has(event.animationName)) return;
  const pulse = event.target.getAnimations().find(animation => animation.animationName === event.animationName);
  if (pulse && pulse.startTime !== 0) pulse.startTime = 0;
});
const receiptLabels = {pending: "待接收", writing: "写入中", delivered: "已送达", delivery_uncertain: "待核对"};
function logTimestamp(value) {
  if (!value) return "—";
  const d = new Date(value);
  if (Number.isNaN(d.getTime())) return "—";
  const two = n => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${two(d.getMonth() + 1)}-${two(d.getDate())} ${two(d.getHours())}:${two(d.getMinutes())}:${two(d.getSeconds())}`;
}
let timelineSignature;

function renderTimeline(entries) {
  const panel = byId("collab-log"), list = byId("topic-timeline");
  panel.hidden = !selectedTopicId;
  const signature = JSON.stringify([selectedTopicId, entries]);
  if (signature === timelineSignature) return;
  timelineSignature = signature;
  const latest = entries[0];
  byId("log-latest").textContent = latest ? latest.from + " → " + latest.to + " · " + latest.summary : "暂无记录";
  panel.classList.toggle("has-messages", !!latest);
  const focused = list.contains(document.activeElement) && document.activeElement.dataset.message;
  list.replaceChildren(...entries.map(entry => {
    const route = entry.from + " → " + entry.to;
    const status = entry.acknowledged ? "已忽略" : receiptLabels[entry.state] || entry.state;
    const button = node("button", "timeline-entry");
    button.type = "button";
    button.dataset.message = entry.id;
    button.dataset.state = entry.acknowledged ? "ignored" : entry.state;
    const stamp = logTimestamp(entry.createdAt);
    const time = node("span", "log-time", stamp);
    time.title = entry.createdAt ? stamp : "历史时间未记录";
    button.title = stamp + " · " + route + " · " + entry.summary + " · " + status;
    button.append(time, node("span", "log-route", route), node("span", "log-summary", entry.summary), node("span", "log-state", status));
    button.onclick = async () => {
      try { showPreview(await request("/messages/" + entry.id)); }
      catch (error) { feedback("connection-error", error.message); }
    };
    return button;
  }));
  if (focused) list.querySelector('[data-message="' + CSS.escape(focused) + '"]')?.focus({preventScroll: true});
  list.scrollTop = 0;
}

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

function messageAnchor(id) {
  const pane = panes.get(id);
  if (!pane) return null;
  if (pane.agent.topicId !== selectedTopicId) {
    const owner = snapshot.topics.find(t => t.id === pane.agent.topicId);
    if (owner && projectKey(owner.project) !== activeProjectKey) {
      const project = projectTab(owner.project);
      return project?.getClientRects().length ? project : byId("project-collapse");
    }
    const topic = document.querySelector('.topic-choice[data-topic="' + pane.agent.topicId + '"]');
    if (!topic?.getClientRects().length) return byId("mobile-topic");
    const member = topic.querySelector('.topic-agent-tag[data-agent="' + CSS.escape(id) + '"] .member-name');
    if (member) {
      const bounds = member.getBoundingClientRect(), group = member.closest(".topic-agents").getBoundingClientRect();
      if (bounds.width && bounds.left >= group.left && bounds.right <= group.right + 1) return member;
    }
    return topic.querySelector(".topic-label") || topic;
  }
  return pane.element.querySelector(".agent-name") || pane.element.querySelector(".agent-light") || pane.element;
}

async function flyMessage(entry) {
  for (let pass = 0; pass < 2; pass++) {
    if (document.visibilityState !== "visible") return;
    const from = messageAnchor(entry.source), to = messageAnchor(entry.target);
    if (!from?.getClientRects().length || !to?.getClientRects().length || from === to) return;
    const source = from.getBoundingClientRect(), target = to.getBoundingClientRect();
    const x = source.x + source.width / 2, y = source.y + source.height / 2;
    const tx = target.x + target.width / 2, ty = target.y + target.height / 2;
    const arc = Math.max(56, Math.min(140, Math.hypot(tx - x, ty - y) * 0.28));
    const envelope = node("span", "message-flight");
    envelope.setAttribute("aria-hidden", "true");
    envelope.innerHTML = '<svg viewBox="0 0 24 18" aria-hidden="true"><rect x="1" y="1" width="22" height="16" rx="3"/><path d="m2 3 10 7L22 3"/></svg>';
    envelope.style.offsetPath = "path('M " + x + " " + y + " Q " + (x + tx) / 2 + " " +
      Math.max(14, Math.min(y, ty) - arc) + " " + tx + " " + ty + "')";
    document.body.append(envelope);
    const glow = [{textShadow:"0 0 8px var(--accent)"},{textShadow:"none"}];
    const pulse = [{backgroundColor:"var(--wash)", boxShadow:"inset 0 0 0 1px var(--accent)"},
      {backgroundColor:"transparent", boxShadow:"inset 0 0 0 1px transparent"}];
    from.animate(glow, 280);
    from.closest(".topic-choice, .project-button")?.animate(pulse, 320);
    try {
      await envelope.animate([{offsetDistance:"0%",transform:"scale(.75)"},{offsetDistance:"50%",transform:"scale(1.15)"},
        {offsetDistance:"100%",transform:"scale(.88)"}], {duration:1150, easing:"ease-in-out"}).finished;
    } catch { return; }
    finally { envelope.remove(); }
    if (to.isConnected) {
      to.animate(glow, 320);
      to.closest(".pane-header, .topic-choice, .project-button")?.animate(pulse, 320);
    }
  }
}

function receiptEffect(entry, delivered) {
  const target = messageAnchor(entry.target);
  if (!target?.getClientRects().length || document.visibilityState !== "visible") return;
  const pane = panes.get(entry.target)?.element;
  if (delivered) {
    const surface = pane?.getClientRects().length ? pane : target;
    surface.animate([{boxShadow:"inset 0 0 0 2px var(--accent)"},{boxShadow:"inset 0 0 0 2px transparent"}], 340);
    return;
  }
  flyMessage(entry);
  const badge = target.querySelector(".tag-inbox:not([hidden])") || target;
  const frames = badge === target
    ? [{backgroundColor:"var(--wash)"},{backgroundColor:"transparent"}]
    : [{transform:"scale(1)"},{transform:"scale(1.18)"},{transform:"scale(1)"}];
  badge.animate(frames, 260);
}

// 只比较相邻快照。整页重载、切换话题及标为已读均不重播历史交接。
function animateMessages(previous, current) {
  if (!previous) return;
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
  for (const entry of messages(current).values()) {
    const old = before.get(entry.id);
    if (!old) receiptEffect(entry, false);
    if (entry.state === "delivered" && old?.state !== "delivered") receiptEffect(entry, true);
  }
}

"use strict";

const byId = id => document.getElementById(id);
const engineNames = { codex: "Codex", copilot: "GitHub Copilot", claude: "Claude Code" };
const bytes = new TextEncoder();
const panes = new Map();
const installingEngines = new Set();
const selectedEngine = () => byId("new-form").elements.engine.value;
const layoutKey = "lx.agent.workbench.layout";
const narrow = matchMedia("(width < 900px)");
let layout;
let snapshot;
let refreshing;
const singleColumn = matchMedia("(width < 720px)");
let catalogRequest;
let models = [];
let selectedProject;
let folderRequest;
let folderListing;
let pickFolder;
let fileRequest;
let fileTimer;
let filePane;
const selectedFiles = new Set();

function readLayout(topic) {
  let saved;
  try {
    const scoped = topic && localStorage.getItem(layoutKey + "." + topic.id);
    saved = JSON.parse(scoped ?? (topic?.name === "默认话题" ? localStorage.getItem(layoutKey) : null));
  }
  catch { /* 本地存储不可用时，当前页面仍可切换布局。 */ }
  return {
    primaryWindowId: saved?.primaryWindowId ?? null,
    activeWindowId: saved?.activeWindowId ?? null,
    collapsedIds: Array.isArray(saved?.collapsedIds) ? saved.collapsedIds : [],
    splitRatio: Number.isFinite(saved?.splitRatio) ? Math.min(1, Math.max(0, saved.splitRatio)) : .45
  };
}

function saveLayout() {
  if (!selectedTopicId) return;
  try { localStorage.setItem(layoutKey + "." + selectedTopicId, JSON.stringify(layout)); }
  catch { /* 浏览器禁用存储不会影响当前终端。 */ }
}

function node(tag, className, text) {
  const element = document.createElement(tag);
  if (className) element.className = className;
  if (text != null) element.textContent = text;
  return element;
}

async function request(path, { method = "GET", body, signal } = {}) {
  const options = { method, signal };
  if (body != null) {
    options.headers = { "Content-Type": "application/json" };
    options.body = JSON.stringify(body);
  }
  const response = await fetch("/api" + path, options);
  const result = response.status === 204 ? null : await response.json().catch(() => null);
  if (!response.ok) throw new Error(result?.error || "操作失败，请检查工作台连接");
  return result;
}

function feedback(id, message) {
  const target = typeof id === "string" ? byId(id) : id;
  target.textContent = message || "";
  target.hidden = !message;
}

function folderName(path) {
  return path.replace(/[\\/]+$/, "").split(/[\\/]/).at(-1) || path;
}

function setBusy(dialog, busy) {
  dialog.dataset.busy = String(busy);
  dialog.querySelectorAll("[data-close]").forEach(button => { button.disabled = busy; });
}

function refresh() {
  if (!refreshing) {
    const query = selectedTopicId ? "?topic=" + encodeURIComponent(selectedTopicId) + "&recent=30" : "";
    refreshing = request("/state" + query).then(data => {
      const previous = snapshot;
      const formOpen = byId("new-dialog").open;
      const samePanes = data.windows.length === panes.size && data.windows.every(w => {
        const pane = panes.get(w.agent.id);
        return pane && pane.generation === w.terminal && pane.view.agent.online === w.agent.online;
      });
      snapshot = data;
      if (formOpen && samePanes) {
        // 表单遮住工作区时，继续处理 WebSocket、收件和提示符；关闭后一次绘制最新快照。
        for (const view of data.windows) syncTerminal(panes.get(view.agent.id), view);
      } else {
        normalizeActivities(data);
        render();
        if (!formOpen) animateMessages(previous, data);
      }
      byId("connection").textContent = "已连接";
      byId("connection").title = "已连接";
      byId("connection").setAttribute("aria-label", "已连接");
      byId("connection").dataset.state = "online";
      feedback("connection-error");
    }).catch(error => {
      byId("connection").textContent = "连接中断";
      byId("connection").title = "连接中断";
      byId("connection").setAttribute("aria-label", "连接中断");
      byId("connection").dataset.state = "offline";
      feedback("connection-error", error.message);
    }).finally(() => { refreshing = undefined; });
  }
  return refreshing;
}

function normalizeActivities(data) {
  for (const view of data.windows) {
    const pane = panes.get(view.agent.id);
    if (!pane || pane.replaying || !view.agent.online || pane.socket?.readyState !== WebSocket.OPEN) continue;
    const screen = observePrompt(pane).screen;
    if (screen === "blocked") view.activity = "waiting";
    else if (screen === "running") view.activity = "working";
    else if (screen === "clear" || screen === "draft") view.activity = "idle";
  }
  const byTopic = new Map(data.topics.map(t => [t.id, []]));
  for (const view of data.windows) byTopic.get(view.agent.topicId)?.push(view);
  for (const topic of data.topics) {
    if (topic.status === "finished") { topic.activity = "finished"; continue; }
    const list = byTopic.get(topic.id) || [];
    if (!list.length || list.every(w => !w.agent.online)) topic.activity = "offline";
    else if (list.some(w => w.activity === "working")) topic.activity = "working";
    else if (list.some(w => w.activity === "waiting" || w.inbox.some(m => m.state !== "delivered" && !(m.state === "delivery_uncertain" && m.acknowledged)))) topic.activity = "waiting";
    else topic.activity = "idle";
  }
}

function focusAgent(id) {
  const pane = panes.get(id);
  if (!pane) return;
  const collapsed = layout.collapsedIds.includes(id);
  if (collapsed || layout.activeWindowId !== id) {
    if (collapsed) layout.collapsedIds = layout.collapsedIds.filter(item => item !== id);
    layout.activeWindowId = id;
    saveLayout();
    renderLayout(collapsed ? pane : null);
  }
  fitPane(pane);
  if (!collapsed) pane.element.scrollIntoView({ block: "nearest", inline: "nearest" });
  if (pane.view.agent.online) pane.term.focus();
  else pane.element.querySelector(".start-pane").focus();
}

function render() {
  const { windows } = snapshot;
  renderTopics();
  const topic = selectedTopic();
  const populated = windows.some(view => view.agent.topicId === selectedTopicId);
  byId("new-agent").disabled = topic?.status !== "active";
  byId("new-agent").hidden = topic?.status !== "active";
  byId("empty").hidden = populated;
  byId("workspace-layout").hidden = !populated;
  byId("empty-title").textContent = topic ? "暂无 Agent" : "暂无话题";
  const emptyPath = topic?.project || "";
  byId("empty-description").textContent = emptyPath;
  byId("empty-description").hidden = !emptyPath;
  byId("empty-create").textContent = topic ? "＋ Agent" : "＋ 新建话题";
  byId("empty-create").disabled = topic?.status === "finished";
  const ids = new Set(windows.map(view => view.agent.id));
  for (const [id, pane] of panes) {
    if (ids.has(id)) continue;
    panes.delete(id);
    clearTimeout(pane.runningTimer);
    pane.observer.disconnect();
    pane.socket?.close();
    pane.term.dispose();
    pane.element.remove();
  }
  for (const view of windows) {
    if (!panes.has(view.agent.id)) {
      panes.set(view.agent.id, makePane(view.agent));
      loadQuota(view.agent.engine);
    }
    updatePane(panes.get(view.agent.id), view);
  }
  renderLayout();
}

function renderLayout(moving = null) {
  const ids = snapshot.windows.filter(({ agent }) => agent.topicId === selectedTopicId).map(({ agent }) => agent.id);
  const before = new Map();
  for (const pane of panes.values()) {
    if (pane.agent.topicId !== selectedTopicId) {
      pane.element.hidden = true;
      pane.layoutFlight?.cancel();
    } else if (moving && !pane.element.hidden) {
      before.set(pane, pane.element.getBoundingClientRect());
      pane.layoutMotion?.cancel();
      pane.layoutFlight?.cancel();
    }
  }
  const primary = ids.includes(layout.primaryWindowId) ? layout.primaryWindowId : null;
  const folded = layout.collapsedIds.filter(id => id !== primary && ids.includes(id));
  if (layout.primaryWindowId !== primary || folded.length !== layout.collapsedIds.length ||
      (layout.activeWindowId && !ids.includes(layout.activeWindowId))) {
    layout.primaryWindowId = primary;
    layout.collapsedIds = folded;
    if (!ids.includes(layout.activeWindowId)) layout.activeWindowId = null;
    saveLayout();
  }
  const left = singleColumn.matches ? null : primary;
  const hasTiles = ids.some(id => id !== left && !folded.includes(id));
  const leftPanes = byId("primary-panes"), rightPanes = byId("panes");
  const tray = byId("collapsed-panes");
  tray.hidden = !folded.length;
  const split = !!left && hasTiles;
  byId("workspace-layout").dataset.split = String(split);
  byId("pane-divider").hidden = !split;
  leftPanes.hidden = !left;
  byId("collaboration").hidden = !hasTiles;

  const refitPanes = [];
  ids.forEach((id, index) => {
    const pane = panes.get(id), el = pane.element;
    const collapsed = folded.includes(id);
    const target = collapsed ? tray : id === left ? leftPanes : rightPanes;
    const refit = el.parentElement !== target || el.hidden || el.dataset.collapsed !== String(collapsed);
    if (el.parentElement !== target) target.append(el);
    el.hidden = false;
    el.style.order = id === primary ? "-1" : index;
    el.dataset.collapsed = String(collapsed);
    pane.surface.hidden = collapsed;
    const toggle = el.querySelector(".toggle-collapse");
    toggle.setAttribute("aria-expanded", String(!collapsed));
    toggle.title = collapsed ? "展开终端" : "缩小到顶部";
    toggle.textContent = collapsed ? "⤢ 展开" : "⤡ 缩小";
    const isPrimary = id === primary;
    toggle.hidden = isPrimary;
    el.querySelector(".primary-mark").hidden = !isPrimary;
    const primaryLabel = isPrimary ? "取消主对话" : "设为主对话";
    if (pane.primaryButton.textContent !== primaryLabel) {
      pane.primaryButton.textContent = primaryLabel;
      pane.toolbarWidth = null;
    }
    if (refit && !collapsed) refitPanes.push(pane);
  });
  // 所有窗格归位后再按最终列宽收纳按钮、调整 ConPTY 尺寸。
  for (const id of ids) fitPaneToolbar(panes.get(id));
  for (const pane of refitPanes) fitPane(pane);
  if (!moving) return;
  moving.element.scrollIntoView({block:"nearest", inline:"nearest"});
  animatePaneLayout(before, moving);
}

function animatePaneLayout(before, moving) {
  for (const [pane, from] of before) {
    const el = pane.element, to = el.getBoundingClientRect();
    if (!from.width || !from.height || !to.width || !to.height ||
        (from.x === to.x && from.y === to.y && from.width === to.width && from.height === to.height)) continue;
    const transform = `translate(${from.x - to.x}px, ${from.y - to.y}px) scale(${from.width / to.width}, ${from.height / to.height})`;
    const frames = [{transform, transformOrigin:"0 0"}, {transform:"none", transformOrigin:"0 0"}];
    const timing = {duration:240, easing:"cubic-bezier(0.22, 1, 0.36, 1)"};
    pane.layoutMotion = el.animate(frames, timing);
    if (pane !== moving) continue;
    // 两处滚动容器会裁剪越界的动画。用不可交互的标题轮廓连接两端，终端本体留在目标布局。
    const outline = el.cloneNode(false);
    outline.removeAttribute("data-window");
    outline.classList.add("pane-flight");
    outline.inert = true;
    outline.setAttribute("aria-hidden", "true");
    outline.append(el.querySelector(".pane-header").cloneNode(true));
    Object.assign(outline.style, {left:to.x+"px", top:to.y+"px", width:to.width+"px", height:to.height+"px"});
    document.body.append(outline);
    const flight = outline.animate([frames[0], {...frames[1], opacity:0}], timing);
    pane.layoutFlight = flight;
    flight.onfinish = flight.oncancel = () => {
      outline.remove();
      if (pane.layoutFlight === flight) pane.layoutFlight = null;
    };
  }
}

function togglePrimary(pane) {
  const id = pane.agent.id;
  layout.primaryWindowId = layout.primaryWindowId === id ? null : id;
  saveLayout();
  renderLayout();
}

function fitPaneToolbar(pane) {
  const {actions, more, moreMenu, toolbarItems} = pane;
  const available = actions.clientWidth;
  if (!available || available === pane.toolbarWidth) return;
  pane.toolbarWidth = available;
  const focused = document.activeElement;
  const hadMenuFocus = moreMenu.contains(focused);
  if (moreMenu.matches(":popover-open")) moreMenu.hidePopover();
  // 把上一轮收纳的尾部控件放回原位，按当前字体与实际文案测量。
  for (const item of Array.from(moreMenu.children)) actions.insertBefore(item, more);
  more.hidden = false;
  const gap = parseFloat(getComputedStyle(actions).columnGap);
  const widths = toolbarItems.map(item => parseFloat(getComputedStyle(item).width));
  const total = widths.reduce((sum, width) => sum + width, gap * (widths.length - 1));
  more.hidden = total <= available;
  if (!more.hidden) {
    let used = parseFloat(getComputedStyle(more).width);
    for (let index = 0; index < widths.length; index++) {
      used += widths[index] + gap;
      if (used > available) {
        moreMenu.append(...toolbarItems.slice(index));
        break;
      }
    }
  }
  if (moreMenu.contains(focused)) more.focus({preventScroll:true});
  else if (focused === more && more.hidden) toolbarItems.at(-1).focus({preventScroll:true});
  else if (hadMenuFocus) focused.focus({preventScroll:true});
}

function applySplitRatio() {
  byId("workspace-layout").style.setProperty("--split-ratio", layout.splitRatio);
  byId("pane-divider").setAttribute("aria-valuenow", String(Math.round(layout.splitRatio * 100)));
}

function resizeSplit(position) {
  const area = byId("workspace-layout");
  const available = area.clientWidth - byId("pane-divider").offsetWidth;
  const minimum = parseFloat(getComputedStyle(area).getPropertyValue("--pane-min"));
  layout.splitRatio = Math.min(available - minimum, Math.max(minimum, position)) / available;
  applySplitRatio();
}

function attachLayoutControls() {
  singleColumn.addEventListener("change", () => { if (snapshot) renderLayout(); });
  const divider = byId("pane-divider");
  let offset;
  divider.addEventListener("pointerdown", event => {
    if (!event.isPrimary || event.button !== 0) return;
    offset = event.clientX - divider.getBoundingClientRect().left;
    divider.setPointerCapture(event.pointerId);
  });
  divider.addEventListener("pointermove", event => {
    if (!divider.hasPointerCapture(event.pointerId)) return;
    resizeSplit(event.clientX - byId("workspace-layout").getBoundingClientRect().left - offset);
  });
  divider.addEventListener("lostpointercapture", saveLayout);
  divider.addEventListener("keydown", event => {
    const step = { ArrowLeft: -24, ArrowRight: 24 }[event.key];
    if (!step) return;
    event.preventDefault();
    resizeSplit(byId("primary-panes").getBoundingClientRect().width + step);
    saveLayout();
  });
}

function engineMark(engine) {
  const mark = node("span", "engine-mark", engine === "claude" ? "Cl" : engine === "codex" ? "C" : "GH");
  mark.dataset.engine = engine;
  mark.setAttribute("aria-hidden", "true");
  return mark;
}

function makePane(agent) {
  const element = byId("pane-template").content.firstElementChild.cloneNode(true);
  element.dataset.window = agent.id;
  element.dataset.engine = agent.engine;
  const engine = element.querySelector(".tile-engine");
  engine.textContent = agent.engine === "copilot" ? "Copilot" : engineNames[agent.engine];
  engine.title = engineNames[agent.engine];
  const viewport = element.querySelector(".terminal-viewport");
  const term = new Terminal({
    fontFamily: '"Cascadia Mono", Consolas, "Microsoft YaHei UI", monospace',
    fontSize: 13, cursorBlink: true, scrollback: 4000,
    theme: { background: "#0a0a0d", foreground: "#e4e4e7", cursor: "#f4f4f5", selectionBackground: "#27272a" },
    windowsPty: { backend: "conpty" }
  });
  const fit = new FitAddon.FitAddon();
  term.loadAddon(fit);
  byId("panes").append(element);
  term.open(viewport);
  const surface = element.querySelector(".terminal-surface");
  surface.id = "terminal-" + agent.id;
  const collapse = element.querySelector(".toggle-collapse");
  collapse.setAttribute("aria-controls", surface.id);
  collapse.onclick = () => {
    if (layout.collapsedIds.includes(agent.id)) return focusAgent(agent.id);
    layout.collapsedIds.push(agent.id);
    saveLayout();
    renderLayout(pane);
    collapse.focus({preventScroll:true});
  };
  const actions = element.querySelector(".terminal-actions"), more = actions.querySelector(".pane-more");
  const moreMenu = actions.querySelector(".pane-more-menu");
  more.popoverTargetElement = moreMenu;
  const toolbarItems = Array.from(actions.children).filter(item => item !== more && item.matches("button, label"));
  const pane = { agent, element, term, fit, surface, actions, more, moreMenu, toolbarItems,
    primaryButton:actions.querySelector(".set-primary"), generation:undefined, revision:0 };
  moreMenu.addEventListener("click", event => {
    if (event.target.closest("button:not(.shortcuts-toggle), input") && moreMenu.matches(":popover-open")) moreMenu.hidePopover();
  });
  pane.observer = new ResizeObserver(() => { fitPaneToolbar(pane); fitPane(pane); });
  pane.observer.observe(viewport);
  pane.observer.observe(actions);
  term.onData(text => {
    text = text.replace(replayReports, "");
    if (text) sendBytes(pane, bytes.encode(text));
  });
  term.onBinary(text => {
    const data = text.replace(replayReports, "");
    if (data) sendBytes(pane, Uint8Array.from(data, character => character.charCodeAt(0)));
  });
  term.attachCustomKeyEventHandler(event => {
    if (event.type !== "keydown" || !event.altKey || !event.shiftKey || event.ctrlKey || !pane.view?.agent.online) return true;
    if (event.code === "KeyA") openPeers(pane);
    else if (event.code === "KeyF") openFiles(pane);
    else return true;
    event.preventDefault();
    return false;
  });

  element.querySelector(".insert-agent").addEventListener("click", () => openPeers(pane));
  element.querySelector(".insert-file").addEventListener("click", () => openFiles(pane));
  const menu = element.querySelector(".shortcut-menu");
  element.querySelector(".shortcuts-toggle").popoverTargetElement = menu;
  menu.addEventListener("toggle", event => {
    if (event.newState === "open") loadShortcuts(pane, menu);
  });
  element.querySelector(".set-primary").addEventListener("click", () => togglePrimary(pane));
  element.querySelector(".pane-sessions").addEventListener("click", () => openPaneSessions(pane));
  element.querySelector(".rename-agent").onclick = () => {
    const dialog = byId("rename-dialog"), input = byId("rename-name");
    dialog.dataset.targetId = agent.id;
    input.value = pane.agent.name;
    feedback("rename-error");
    dialog.showModal();
    input.select();
  };
  element.querySelector(".pane-quota").addEventListener("click", () => loadQuota(agent.engine, true));
  element.querySelector(".preview-inbox").addEventListener("click", () => showPreview(pane.message));
  const ignore = element.querySelector(".ack-inbox");
  ignore.onclick = async () => {
    if (!canIgnore(pane.message)) return;
    ignore.disabled = true;
    try {
      await request(windowRoute(pane) + "/inbox/" + pane.message.id + "/acknowledge", {method:"POST"});
      await refreshing;
      await refresh();
    } catch (error) {
      pane.receiptErrorId = null;
      feedback(element.querySelector(".pane-error"), error.message);
    } finally { ignore.disabled = false; }
  };
  element.querySelector(".next-inbox").addEventListener("click", () => {
    const messages = pane.view.inbox;
    const current = messages.findIndex(item => item.id === pane.message?.id);
    pane.selectedMessage = messages[(current + 1) % messages.length].id;
    updatePane(pane, pane.view);
  });
  element.querySelector(".write-inbox").onclick = () => {
    const messageId = pane.message.id;
    return paneAction(pane, () => request(windowRoute(pane) + "/input", { method:"POST", body:{messageId} }), messageId);
  };
  const start = element.querySelector(".start-pane");
  start.addEventListener("click", async () => {
    start.disabled = true;
    start.textContent = "正在打开…";
    try { await paneAction(pane, () => request(windowRoute(pane) + "/start", {method: "POST"})); }
    finally { start.disabled = false; start.textContent = "打开终端"; }
  });
  element.querySelector(".close-pane").addEventListener("click", () => paneAction(pane, () =>
    request(windowRoute(pane), { method: "DELETE" })));
  element.querySelector(".auto-receive input").addEventListener("change", event => paneAction(pane, () =>
    request(windowRoute(pane) + "/auto-receive", { method: "PUT", body: { enabled: event.target.checked } })));
  return pane;
}

function windowRoute(pane) { return "/windows/" + pane.agent.id; }
// [T09c] 实时输出和连接重放都可能触发多个终端查询回复。
function canIgnore(message) {
  return message?.state === "pending" || message?.state === "delivery_uncertain";
}

function updatePane(pane, view) {
  if (pane.element.querySelector(".tile-name").textContent !== "@" + view.agent.name) {
    const agent = view.agent, label = "@" + agent.name;
    const name = pane.element.querySelector(".tile-name");
    name.textContent = label;
    name.title = label + " · " + engineNames[agent.engine] + " · " + agent.project;
    pane.element.setAttribute("aria-label", label + " 终端");
    pane.element.querySelector(".terminal-viewport").setAttribute("aria-label", agent.name + " 原生终端");
  }
  syncTerminal(pane, view);
  if (pane.receiptErrorId && !view.inbox.some(item => item.id === pane.receiptErrorId)) {
    feedback(pane.element.querySelector(".pane-error"));
    pane.receiptErrorId = null;
  } else if (!view.inbox.some(item => item.state === "delivery_uncertain")) clearSubmitError(pane);
  const element = pane.element;
  const online = view.agent.online;
  const dot = element.querySelector(".agent-light");
  element.dataset.activity = view.activity;
  const status = view.activity === "offline" ? "未启动" : view.activity === "waiting" ? "待处理" : activityLabels[view.activity];
  dot.title = status;
  const overlay = element.querySelector(".terminal-overlay");
  overlay.hidden = online;
  if (!online) {
    overlay.querySelector(".offline-name").textContent = "@" + view.agent.name;
    const status = overlay.querySelector(".exit-status");
    status.hidden = !view.terminal;
    status.textContent = view.terminal ? "终端已退出" + (view.exitCode ? "（" + view.exitCode + "）" : "") : "";
  }
  element.querySelector(".status-label").textContent = status;
  element.querySelector(".auto-receive input").checked = view.agent.autoReceive;
  element.querySelector(".insert-agent").disabled = !online;
  element.querySelector(".insert-file").disabled = !online;
  element.querySelector(".shortcuts-toggle").disabled = !online;
  if (!online && element.querySelector(".shortcut-menu:popover-open")) element.querySelector(".shortcut-menu").hidePopover();

  const inbox = element.querySelector(".inbox");
  const pending = view.inbox.find(item => item.state === "pending");
  const message = view.inbox.find(item => item.id === pane.selectedMessage) || pending || view.inbox[0];
  pane.message = message;
  inbox.hidden = !message;
  if (!message) return;
  inbox.dataset.state = message.state;
  const label = message.state === "delivery_uncertain" ? "送达待核对" : "消息";
  inbox.querySelector(".inbox-caption").textContent =
    (message.fromTopic ? "跨话题 · " + message.fromTopic + " · " : "") +
    "来自 @" + message.from + " 的" + label + (view.inbox.length > 1 ? " · " + view.inbox.length + " 条" : "");
  inbox.querySelector(".next-inbox").hidden = view.inbox.length < 2;
  inbox.querySelector(".ack-inbox").hidden = !canIgnore(message);
  const write = inbox.querySelector(".write-inbox");
  write.hidden = message.state !== "pending";
  write.disabled = !online;
  write.title = online ? "写入当前终端并提交这条消息" : "终端已退出";
}

function clearSubmitError(pane) {
  const error = pane.element.querySelector(".pane-error");
  if (error.textContent.endsWith("请核对终端")) {
    feedback(error);
    pane.receiptErrorId = null;
  }
}

async function paneAction(pane, action, messageId = null) {
  pane.receiptErrorId = null;
  feedback(pane.element.querySelector(".pane-error"));
  const previousFocus = document.activeElement;
  try {
    await action();
    await refresh();
    const currentFocus = document.activeElement;
    if (!pane.surface.hidden && !pane.element.hidden && panes.has(pane.agent.id) && (currentFocus === previousFocus || currentFocus === document.body)) pane.term.focus();
  } catch (error) {
    pane.receiptErrorId = messageId;
    feedback(pane.element.querySelector(".pane-error"), error.message);
    await refresh();
  }
}

function insert(pane, text) {
  return request(windowRoute(pane) + "/input", { method: "POST", body: { text } });
}

// [T01] 每次打开均取本引擎的当前清单；选中只复用原生输入行的单行插入入口。
async function loadShortcuts(pane, menu) {
  const loading = node("p", "selection-empty", "正在读取…");
  menu.replaceChildren(loading);
  const engine = pane.agent.engine;
  try {
    const items = await request("/shortcuts/" + engine);
    if (!menu.contains(loading) || !menu.matches(":popover-open")) return;
    const buttons = items.map(({ command, note }) => {
      const button = node("button", "selection-item");
      button.type = "button";
      button.append(node("code", "", command), node("small", "", note));
      button.addEventListener("click", () => {
        menu.hidePopover();
        paneAction(pane, () => insert(pane, command));
      });
      return button;
    });
    const manage = node("button", "text-button", "管理快捷指令");
    manage.type = "button";
    manage.addEventListener("click", () => {
      menu.hidePopover();
      const dialog = byId("shortcuts-dialog");
      dialog.dataset.engine = engine;
      byId("shortcuts-title").textContent = engineNames[engine] + " 快捷指令";
      showShortcutChoices(items);
      feedback("shortcut-error");
      dialog.showModal();
    });
    menu.replaceChildren(...buttons, manage);
  } catch (error) { loading.textContent = error.message; }
}

function showShortcutChoices(items, selected = "") {
  const choice = byId("shortcut-choice");
  choice.replaceChildren(new Option("新增指令", ""), ...items.map(({ command, note }) => {
    const option = new Option(command, command);
    option.dataset.note = note;
    return option;
  }));
  choice.value = selected;
  editShortcut();
}

function editShortcut() {
  const choice = byId("shortcut-choice");
  const command = byId("shortcut-command");
  command.value = choice.value;
  command.readOnly = !!choice.value;
  byId("shortcut-note").value = choice.selectedOptions[0]?.dataset.note || "";
  byId("shortcut-delete").disabled = !choice.value;
}

async function changeShortcut(action) {
  const dialog = byId("shortcuts-dialog");
  if (dialog.dataset.busy === "true") return;
  const command = byId("shortcut-command").value;
  const body = action === "reset" ? { action } : { action, command, note: byId("shortcut-note").value };
  setBusy(dialog, true);
  feedback("shortcut-error");
  try {
    const items = await request("/shortcuts/" + dialog.dataset.engine, { method: "POST", body });
    showShortcutChoices(items, action === "save" ? command : "");
  } catch (error) { feedback("shortcut-error", error.message); }
  finally { setBusy(dialog, false); }
}

byId("shortcut-choice").addEventListener("change", editShortcut);
byId("shortcut-form").addEventListener("submit", event => { event.preventDefault(); changeShortcut("save"); });
byId("shortcut-delete").addEventListener("click", () => changeShortcut("delete"));
byId("shortcuts-reset").addEventListener("click", () => changeShortcut("reset"));

function openPeers(pane) {
  const select = byId("peer-topic");
  select.replaceChildren(...snapshot.topics.map(t => new Option(t.name, t.id)));
  select.value = pane.agent.topicId;
  select.onchange = () => renderPeers(pane, select.value);
  renderPeers(pane, select.value);
  byId("agents-dialog").showModal();
}

function renderPeers(pane, topicId) {
  const list = byId("agent-options");
  const topic = snapshot.topics.find(t => t.id === topicId);
  list.replaceChildren(...snapshot.windows.filter(({ agent }) => agent.id !== pane.agent.id && agent.topicId === topicId).map(({ agent }) => {
    const button = node("button", "selection-item");
    button.type = "button";
    button.append(engineMark(agent.engine), node("strong", "", "@" + agent.name),
      node("small", "", folderName(agent.project) + " · " + (agent.online ? "在线" : "离线")));
    button.addEventListener("click", () => {
      byId("agents-dialog").close();
      const address = "@" + (topicId === pane.agent.topicId ? "" : topic.name + "/") + agent.name;
      paneAction(pane, () => insert(pane, address + " "));
    });
    return button;
  }));
  if (!list.childElementCount) list.append(node("p", "selection-empty", "还没有其他 Agent 窗口"));
}

function openFiles(pane) {
  filePane = pane;
  selectedFiles.clear();
  byId("file-query").value = "";
  byId("files-dialog").showModal();
  loadFiles();
}

async function loadFiles() {
  fileRequest?.abort();
  const controller = new AbortController();
  fileRequest = controller;
  const list = byId("file-options");
  list.replaceChildren(node("p", "selection-empty", "正在查找…"));
  feedback("file-error");
  refreshSelection();
  try {
    const params = new URLSearchParams({ project: filePane.agent.project, q: byId("file-query").value.trim() });
    const result = await request("/files?" + params, { signal: controller.signal });
    if (controller.signal.aborted) return;
    list.replaceChildren(...result.files.map(path => {
      const item = node("label", "selection-item file-item");
      const box = node("input");
      box.type = "checkbox";
      box.checked = selectedFiles.has(path);
      box.addEventListener("change", () => {
        if (box.checked) selectedFiles.add(path);
        else selectedFiles.delete(path);
        refreshSelection(result.truncated);
      });
      item.append(box, node("span", "", path));
      return item;
    }));
    if (!result.files.length) list.append(node("p", "selection-empty", "没有匹配的文件"));
    refreshSelection(result.truncated);
  } catch (error) {
    if (controller.signal.aborted) return;
    list.replaceChildren();
    feedback("file-error", error.message);
  }
}

function refreshSelection(truncated = false) {
  byId("file-status").textContent = "已选 " + selectedFiles.size + " 个" + (truncated ? " · 可继续缩小搜索范围" : "");
  byId("insert-files").disabled = !selectedFiles.size;
}

byId("file-query").addEventListener("input", () => {
  fileRequest?.abort();
  clearTimeout(fileTimer);
  fileTimer = setTimeout(loadFiles, 180);
});
byId("files-dialog").addEventListener("close", () => { fileRequest?.abort(); clearTimeout(fileTimer); });
byId("insert-files").addEventListener("click", () => {
  const text = Array.from(selectedFiles, path => /\s/.test(path) ? '"' + path + '"' : path).join(" ") + " ";
  const pane = filePane;
  byId("files-dialog").close();
  paneAction(pane, () => insert(pane, text));
});

function showPreview(item) {
  const source = (item.fromTopic ? item.fromTopic + "/" : "") + item.from;
  const sections = ["[来自 @" + source + "] " + item.text];
  if (item.document) sections.push("交接文档：" + item.document);
  if (item.files.length) sections.push("附带文件：\n" + item.files.join("\n"));
  if (item.returnHint) sections.push("回送提示：" + item.returnHint);
  byId("preview-content").textContent = sections.join("\n\n");
  byId("copy-preview").textContent = "复制消息";
  byId("preview-dialog").showModal();
}
byId("copy-preview").addEventListener("click", async () => {
  await navigator.clipboard.writeText(byId("preview-content").textContent);
  byId("copy-preview").textContent = "已复制";
});

function setSelectedProject(path) {
  selectedProject = path;
  byId("agent-project-name").textContent = folderName(path);
  byId("agent-project-path").textContent = path;
  byId("agent-project").title = path;
}

function openCreate() {
  if (!selectedTopic()) { openTopicDialog(); return; }
  byId("new-dialog").dataset.topic = selectedTopicId;
  byId("new-form").reset();
  setSessionSource();
  setSelectedProject(selectedTopic().project);
  feedback("create-error");
  byId("new-dialog").showModal();
  loadCatalogs();
}

function setSessionSource() {
  const usingCopilot = selectedEngine() === "copilot";
  const sources = byId("session-source");
  const latest = sources.querySelector('[value="latest"]');
  latest.hidden = latest.disabled = !usingCopilot;
  sources.value = usingCopilot ? "latest" : "new";
}

function setHistoryOptions(select, choices) {
  select.replaceChildren(new Option(choices.length ? "选择历史会话" : "当前项目没有可选历史会话", ""));
  for (const choice of choices) select.add(new Option(choice.name.slice(0, 100), choice.id));
}

function renderEfforts() {
  const model = models.find(item => item.id === byId("agent-model").value)
    || (byId("agent-model").value ? null : models.find(item => item.isDefault));
  byId("agent-effort").replaceChildren(new Option("跟随模型默认", ""),
    ...(model?.efforts || []).map(effort => new Option(effort, effort)));
  byId("agent-effort").disabled = !model?.efforts?.length;
}

let createHistory = [];
function renderCreateHistory() {
  const select = byId("history-session"), previous = select.value;
  const fork = byId("new-form").elements.historyAction.value === "fork";
  const choices = createHistory.filter(s => fork || !s.mayBeInUse);
  setHistoryOptions(select, choices);
  if (choices.some(s => s.id === previous)) select.value = previous;
  byId("create-agent").disabled = !select.value;
}
byId("history-action").addEventListener("change", renderCreateHistory);
byId("history-session").addEventListener("change", () => { byId("create-agent").disabled = !byId("history-session").value; });

async function loadCatalogs() {
  catalogRequest?.abort();
  const controller = new AbortController();
  catalogRequest = controller;
  const engine = selectedEngine();
  const history = byId("session-source").value === "history";
  const model = byId("agent-model");
  const sessions = byId("history-session");
  const submit = byId("create-agent");
  const summary = byId("catalog-status");
  const mode = byId("agent-mode");
  model.replaceChildren(new Option("跟随引擎默认", ""));
  model.disabled = true;
  models = [];
  renderEfforts();
  mode.replaceChildren(new Option("跟随引擎默认", ""));
  if (engine === "codex") {
    mode.add(new Option("按需请求审批", "on-request"));
    mode.add(new Option("不发起审批请求", "never"));
  } else if (engine === "claude") {
    for (const [id, label] of [["default","默认审批"],["acceptEdits","自动接受编辑"],["plan","计划"],["bypassPermissions","跳过权限确认"]])
      mode.add(new Option(label, id));
  } else {
    mode.add(new Option("Agent", "agent"));
    mode.add(new Option("Autopilot", "autopilot"));
  }
  sessions.replaceChildren(new Option("正在读取…", ""));
  sessions.disabled = !history;
  byId("history-field").hidden = byId("history-action").hidden = !history;
  createHistory = [];
  submit.disabled = true;
  summary.textContent = "正在读取引擎选项…";
  byId("reload-catalog").hidden = true;
  try {
    const installed = await request("/engines", {signal: controller.signal});
    if (controller.signal.aborted) return;
    for (const button of byId("agent-engine").querySelectorAll("[data-install]")) {
      const id = button.dataset.install;
      button.hidden = installed[id];
      button.disabled = installingEngines.has(id);
      button.textContent = button.disabled ? "安装中…" : "安装";
    }
    const fork = byId("history-action").querySelector('[value="fork"]');
    fork.disabled = !Object.hasOwn(installed, "claude");
    if (fork.disabled) byId("new-form").elements.historyAction.value = "resume";
    fork.parentElement.title = fork.disabled ? "重新启动更新后的工作台即可创建分支" : "";
    if (!installed[engine]) {
      summary.textContent = installingEngines.has(engine) ? "正在安装 " + engineNames[engine] + "…" : engineNames[engine] + " 尚未安装";
      byId("engine-quota").replaceChildren();
      byId("refresh-quota").disabled = true;
      sessions.replaceChildren(new Option("请先安装引擎", ""));
      sessions.disabled = true;
      return;
    }
  } catch (error) {
    if (controller.signal.aborted) return;
    summary.textContent = error.message;
    byId("reload-catalog").hidden = false;
    return;
  }
  // 使用引擎默认参数只依赖安装状态。读取模型/历史期间仍允许输入与新建。
  submit.disabled = history;
  loadQuota(engine);
  const params = "?" + new URLSearchParams({ project: selectedProject });
  const query = route => request(route + engine + params, { signal: controller.signal });
  const errors = [];
  const catalog = query("/models/").then(choices => {
    if (controller.signal.aborted) return;
    models = choices;
    choices.forEach(choice => model.add(new Option(choice.name + (choice.isDefault ? "（默认）" : ""), choice.id)));
    model.disabled = false;
    renderEfforts();
  }).catch(error => { errors.push(error.message); });
  const saved = history && request("/sessions/" + engine + params + "&includeBound=true", {signal:controller.signal}).then(choices => {
    if (controller.signal.aborted) return;
    createHistory = choices;
    renderCreateHistory();
  }).catch(error => {
    if (controller.signal.aborted) return;
    sessions.replaceChildren(new Option("历史会话加载失败", ""));
    errors.push(error.message);
  });
  await Promise.all([catalog, saved]);
  if (controller.signal.aborted) return;
  summary.textContent = errors.join(" · ");
  byId("reload-catalog").hidden = !errors.length;
}

byId("new-agent").addEventListener("click", openCreate);
byId("empty-create").addEventListener("click", openCreate);
byId("agent-engine").addEventListener("change", () => {
  setSessionSource();
  loadCatalogs();
});
byId("agent-engine").addEventListener("click", async event => {
  const button = event.target.closest("[data-install]");
  if (!button || button.disabled) return;
  const engine = button.dataset.install;
  if (selectedEngine() !== engine) {
    byId("new-form").elements.engine.value = engine;
    setSessionSource();
  }
  installingEngines.add(engine);
  button.disabled = true;
  button.textContent = "安装中…";
  feedback("create-error");
  loadCatalogs();
  try {
    await request("/engines/" + engine + "/install", {method: "POST"});
    if (!quotas.get(engine)?.loading) quotas.delete(engine);
  } catch (error) { feedback("create-error", error.message); }
  finally {
    installingEngines.delete(engine);
    if (byId("new-dialog").open) loadCatalogs();
  }
});
byId("refresh-quota").addEventListener("click", () => loadQuota(selectedEngine(), true));
byId("session-source").addEventListener("change", loadCatalogs);
byId("agent-model").addEventListener("change", renderEfforts);
byId("reload-catalog").addEventListener("click", loadCatalogs);
byId("new-dialog").addEventListener("close", () => {
  catalogRequest?.abort();
  if (snapshot) { normalizeActivities(snapshot); render(); }
});
byId("new-form").addEventListener("submit", async event => {
  event.preventDefault();
  const dialog = byId("new-dialog");
  const button = byId("create-agent");
  if (dialog.dataset.busy === "true" || button.disabled) return;
  const body = {
    topic: dialog.dataset.topic,
    name: byId("agent-name").value, project: selectedProject, engine: selectedEngine(),
    session: byId("session-source").value === "history" ? byId("history-session").value : null,
    newSession: byId("session-source").value === "new",
    forkSession: byId("session-source").value === "history" && byId("new-form").elements.historyAction.value === "fork",
    model: byId("agent-model").value || null, effort: byId("agent-effort").value || null,
    mode: byId("agent-mode").value || null
  };
  catalogRequest?.abort();
  setBusy(dialog, true);
  button.disabled = true;
  button.textContent = "正在打开…";
  feedback("create-error");
  try {
    const agent = await request(body.forkSession ? "/windows/fork" : "/windows", { method: "POST", body });
    dialog.close();
    await refresh();
    focusAgent(agent.id);
  } catch (error) { feedback("create-error", error.message); }
  finally {
    setBusy(dialog, false);
    button.disabled = false;
    button.textContent = "打开终端";
  }
});

function openFolders(path, onPick) {
  pickFolder = onPick;
  byId("folder-dialog").showModal();
  loadFolders(path);
}

async function loadFolders(path) {
  folderRequest?.abort();
  const controller = new AbortController();
  folderRequest = controller;
  folderListing = undefined;
  const list = byId("folder-list");
  byId("folder-path").value = path || "";
  byId("choose-folder").disabled = true;
  byId("folder-up").disabled = true;
  list.replaceChildren(node("p", "selection-empty", "正在读取…"));
  feedback("folder-error");
  try {
    const query = path ? "?" + new URLSearchParams({ path }) : "";
    const listing = await request("/folders" + query, { signal: controller.signal });
    if (controller.signal.aborted) return;
    folderListing = listing;
    byId("folder-path").value = listing.path || "";
    byId("folder-up").disabled = !listing.path;
    byId("choose-folder").disabled = !listing.path;
    list.replaceChildren(...listing.folders.map(folder => {
      const button = node("button", "selection-item");
      button.type = "button";
      button.append(node("span", "folder-icon"), node("span", "", folder.name), node("small", "", "›"));
      button.addEventListener("click", () => loadFolders(folder.path));
      return button;
    }));
    if (!listing.folders.length) list.append(node("p", "selection-empty", "这个文件夹没有子文件夹"));
  } catch (error) {
    if (controller.signal.aborted) return;
    list.replaceChildren();
    feedback("folder-error", error.message);
  }
}

byId("add-project").addEventListener("click", () => openFolders(snapshot.project, selectProject));
byId("agent-project").addEventListener("click", () => {
  if (byId("new-dialog").dataset.busy !== "true")
    openFolders(selectedProject, path => { setSelectedProject(path); loadCatalogs(); });
});
byId("folder-path-form").addEventListener("submit", event => {
  event.preventDefault();
  loadFolders(byId("folder-path").value.trim() || null);
});
byId("folder-path").addEventListener("input", () => {
  byId("choose-folder").disabled = byId("folder-path").value !== folderListing?.path;
});
byId("folder-disks").addEventListener("click", () => loadFolders(null));
byId("folder-up").addEventListener("click", () => loadFolders(folderListing?.parent || null));
byId("folder-dialog").addEventListener("close", () => folderRequest?.abort());
byId("choose-folder").addEventListener("click", async () => {
  const dialog = byId("folder-dialog");
  const button = byId("choose-folder");
  button.disabled = true;
  setBusy(dialog, true);
  try { await pickFolder(folderListing.path); dialog.close(); }
  catch (error) { feedback("folder-error", error.message); }
  finally { setBusy(dialog, false); button.disabled = false; }
});

byId("rename-form").onsubmit = async event => {
  event.preventDefault();
  const dialog = byId("rename-dialog");
  if (dialog.dataset.busy === "true") return;
  setBusy(dialog, true);
  try {
    await request("/windows/" + dialog.dataset.targetId + "/name", {method:"PUT", body:{name:byId("rename-name").value}});
    dialog.close();
    await refreshing;
    await refresh();
  } catch (error) { feedback("rename-error", error.message); }
  finally { setBusy(dialog, false); }
};

document.querySelectorAll("[data-close]").forEach(button => button.addEventListener("click", () => button.closest("dialog").close()));
document.querySelectorAll("dialog").forEach(dialog => dialog.addEventListener("cancel", event => {
  if (dialog.dataset.busy === "true") event.preventDefault();
}));
function startWorkbench() {
  if (!document.body.hasAttribute("data-starting")) return;
  document.body.removeAttribute("data-starting");
  attachTopicControls();
  attachLogControls();
  attachLayoutControls();
  refresh();
  setInterval(refresh, 750);
}

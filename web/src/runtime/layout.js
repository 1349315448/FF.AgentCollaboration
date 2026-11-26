import { byId } from "./dom.js";
import { movePane, panes } from "../services/terminalRegistry";
import { layout, selectedTopicId, snapshot } from "../store/workbenchStore";
import { fitPane } from "./terminal.js";
import { codeViewport, workspacePreferences } from "./workspaceModes.js";

const layoutKey = "lx.agent.workbench.layout";
const narrow = matchMedia("(width < 900px)");
const singleColumn = matchMedia("(width < 720px)");
function readLayout(topic) {
  let saved;
  try {
    const scoped = topic && localStorage.getItem(layoutKey + "." + topic.id);
    saved = JSON.parse(scoped ?? (topic?.name === "默认话题" ? localStorage.getItem(layoutKey) : null));
  }
  catch { /* 本地存储不可用时，当前页面仍可切换布局。 */ }
  return {
    ...workspacePreferences(saved),
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

function focusAgent(id) {
  const pane = panes.get(id);
  if (!pane) return;
  const code = layout.workspaceMode === "code";
  const collapsed = pane.element.dataset.collapsed === "true";
  if (code) {
    layout.codeActiveWindowId = id;
    layout.codeAgentHidden = false;
    saveLayout();
    renderLayout(collapsed ? pane : null);
  } else if (collapsed || layout.activeWindowId !== id) {
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

let renderLayout = layoutPanes;
function setLayoutProbe(callback) { renderLayout = callback; }

let layoutAnimCount = 0;
let splitDragging = false;
let inspectorDragging = false;
let narrowAgentRegion = false;
let pendingFitIds = new Set();
let pendingRefitPanes = new Set();
let fitFlushRaf = 0;
let activeStageCleanup = null;
const reducedMotion = matchMedia("(prefers-reduced-motion: reduce)");

function isLayoutAnimating() {
  return layoutAnimCount > 0 || splitDragging;
}

function flushActiveStage() {
  if (activeStageCleanup) {
    const cleanup = activeStageCleanup;
    activeStageCleanup = null;
    cleanup();
  }
  for (const pane of panes.values()) {
    pane.element.style.willChange = "";
    if (pane.layoutMotion?.playState === "running") pane.layoutMotion.cancel();
  }
}

function createFlightCard(pane) {
  const card = document.createElement("div");
  card.className = "pane pane-flight";
  card.inert = true;
  card.setAttribute("aria-hidden", "true");
  const activity = pane.element.dataset.activity || "idle";
  card.dataset.activity = activity;
  const title = pane.element.querySelector(".tile-name")?.textContent || pane.agent.name || "Agent";
  const engine = pane.element.querySelector(".tile-engine")?.textContent || "";
  card.innerHTML =
    `<div class="pane-flight-badge">` +
      `<svg class="pane-flight-icon" viewBox="0 0 24 24" aria-hidden="true">` +
        `<rect x="2.5" y="4" width="19" height="16" rx="3" fill="none" stroke="currentColor" stroke-width="1.7"/>` +
        `<path d="M7 9.5l3.2 2.5L7 14.5M12.5 14.5h4.5" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"/>` +
      `</svg>` +
      `<span class="agent-light" aria-hidden="true"></span>` +
      `<span class="pane-flight-title"></span>` +
      (engine ? `<span class="pane-flight-engine"></span>` : "") +
    `</div>`;
  card.querySelector(".pane-flight-title").textContent = title;
  if (engine) card.querySelector(".pane-flight-engine").textContent = engine;
  return card;
}

function animateSiblingsTranslate(before, skipPane = null) {
  // 相邻窗格自适应时只对发生实际位移的窗格做纯位移 translate，绝不用非等比 scale 拉伸终端文字。
  const targets = [];
  for (const [pane, from] of before) {
    if (pane === skipPane) continue;
    const el = pane.element;
    if (el.hidden) continue;
    const to = el.getBoundingClientRect();
    const dx = from.x - to.x, dy = from.y - to.y;
    if (Math.abs(dx) < 1 && Math.abs(dy) < 1) continue;
    targets.push([pane, el, dx, dy]);
  }
  if (!targets.length) return 0;
  const timing = { duration: 160, easing: "cubic-bezier(0.22, 1, 0.36, 1)" };
  for (const [pane, el, dx, dy] of targets) {
    el.style.willChange = "transform, opacity";
    layoutAnimCount++;
    const motion = el.animate(
      [
        { transform: `translate(${dx}px, ${dy}px)`, opacity: 0.88 },
        { transform: "translate(0, 0)", opacity: 1 }
      ],
      timing
    );
    pane.layoutMotion = motion;
    motion.onfinish = motion.oncancel = () => {
      if (pane.layoutMotion === motion) el.style.willChange = "";
      layoutAnimCount = Math.max(0, layoutAnimCount - 1);
      if (layoutAnimCount === 0) schedulePendingFits();
    };
  }
  return targets.length;
}

function schedulePendingFits(ids = [], refitPanes = []) {
  for (const id of ids) pendingFitIds.add(id);
  for (const pane of refitPanes) pendingRefitPanes.add(pane);
  if (isLayoutAnimating() || fitFlushRaf) return;
  fitFlushRaf = requestAnimationFrame(() => {
    fitFlushRaf = 0;
    if (isLayoutAnimating()) return;
    const idsToFit = [...pendingFitIds];
    const panesToRefit = [...pendingRefitPanes];
    pendingFitIds.clear();
    pendingRefitPanes.clear();
    for (const pane of panes.values()) {
      if (!pane.pendingFit) continue;
      pane.pendingFit = false;
      if (!idsToFit.includes(pane.agent.id)) idsToFit.push(pane.agent.id);
      if (!pane.surface.hidden && !panesToRefit.includes(pane)) panesToRefit.push(pane);
    }
    for (const id of idsToFit) {
      const pane = panes.get(id);
      if (pane && !pane.element.hidden) fitPaneToolbar(pane);
    }
    for (const pane of panesToRefit) {
      if (panes.has(pane.agent.id) && !pane.element.hidden && !pane.surface.hidden) fitPane(pane);
    }
  });
}

function layoutPanes(moving = null) {
  flushActiveStage();
  const ids = snapshot.windows.filter(({ agent }) => agent.topicId === selectedTopicId).map(({ agent }) => agent.id);
  const before = new Map();
  let movingFrom = null;
  let movingWasParent = null;
  let movingOldOrder = "";
  for (const pane of panes.values()) {
    if (pane.agent.topicId !== selectedTopicId) {
      pane.element.hidden = true;
      if (pane.term.options.cursorBlink) pane.term.options.cursorBlink = false;
      pane.layoutFlight?.cancel();
    } else if (moving && !pane.element.hidden) {
      const rect = (pane.element.dataset.collapsed === "true" ? topicTag(pane) : pane.element)?.getBoundingClientRect();
      if (!rect) continue;
      before.set(pane, rect);
      if (pane === moving) {
        movingFrom = rect;
        movingWasParent = pane.element.parentElement;
        movingOldOrder = pane.element.style.order;
      }
      pane.layoutMotion?.cancel();
      pane.layoutFlight?.cancel();
    }
  }
  const primary = ids.includes(layout.primaryWindowId) ? layout.primaryWindowId : null;
  const agentFolded = layout.collapsedIds.filter(id => ids.includes(id));
  if (layout.primaryWindowId !== primary || agentFolded.length !== layout.collapsedIds.length ||
      (layout.activeWindowId && !ids.includes(layout.activeWindowId))) {
    layout.primaryWindowId = primary;
    layout.collapsedIds = agentFolded;
    if (!ids.includes(layout.activeWindowId)) layout.activeWindowId = null;
    saveLayout();
  }
  applyWorkspaceMode();
  const code = layout.workspaceMode === "code";
  const viewport = code ? codeViewport(ids, layout) : null;
  const folded = viewport ? viewport.folded : agentFolded;
  if (!inspectorDragging) narrowAgentRegion = byId("workspace-layout").clientWidth < 650;
  const tightPreview = layout.agentInspectorOpen && narrowAgentRegion;
  const left = viewport ? viewport.active : singleColumn.matches || tightPreview || primary && folded.includes(primary) ? null : primary;
  const hasTiles = ids.some(id => id !== left && !folded.includes(id));
  const leftPanes = byId("primary-panes"), rightPanes = byId("panes");
  const tray = byId("terminal-parking");
  const split = !!left && hasTiles;
  byId("workspace-layout").dataset.split = String(split);
  byId("pane-divider").hidden = !split;
  leftPanes.hidden = !left;
  byId("collaboration").hidden = !hasTiles;

  const movingCollapsed = !!(moving && folded.includes(moving.agent.id));
  const holdSlotForCollapse = !!(
    moving && movingCollapsed && !reducedMotion.matches &&
    hasTiles && movingWasParent === rightPanes && movingFrom && movingFrom.width > 0
  );
  let placeholder = null;
  if (holdSlotForCollapse) {
    // 阶段 1：用隐形占位保留原网格槽位，飞入顶部托盘前其余窗口保持原位不提前重排。
    placeholder = document.createElement("div");
    placeholder.className = "pane-placeholder";
    placeholder.style.order = movingOldOrder;
    placeholder.setAttribute("aria-hidden", "true");
    rightPanes.append(placeholder);
  }

  const refitPanes = [];
  ids.forEach((id, index) => {
    const pane = panes.get(id), el = pane.element;
    const collapsed = folded.includes(id);
    const target = collapsed ? tray : id === left ? leftPanes : rightPanes;
    const refit = el.parentElement !== target || el.hidden || el.dataset.collapsed !== String(collapsed);
    movePane(pane, target);
    el.hidden = false;
    el.style.order = id === primary ? "-1" : index;
    el.dataset.collapsed = String(collapsed);
    const tag = topicTag(pane);
    if (tag) {
      const expanded=!collapsed && !(code && layout.codeAgentHidden);
      tag.dataset.expanded = String(expanded);
      tag.querySelector(".member-name")?.setAttribute("aria-expanded",String(expanded));
    }
    pane.surface.hidden = collapsed;
    const wantBlink = !collapsed;
    if (pane.term.options.cursorBlink !== wantBlink) pane.term.options.cursorBlink = wantBlink;
    const toggle = el.querySelector(".toggle-collapse");
    toggle.setAttribute("aria-expanded", String(!collapsed));
    toggle.title = collapsed ? "展开终端" : "缩小到顶部";
    toggle.textContent = collapsed ? "⤢ 展开" : "⤡ 缩小";
    const isPrimary = id === primary;
    if (pane.primaryButton.hidden !== code) {
      pane.primaryButton.hidden = code;
      pane.toolbarWidth = null;
    }
    toggle.hidden = code ? !collapsed : isPrimary;
    el.querySelector(".primary-mark").hidden = !isPrimary;
    const primaryLabel = isPrimary ? "取消主对话" : "设为主对话";
    if (pane.primaryButton.textContent !== primaryLabel) {
      pane.primaryButton.textContent = primaryLabel;
      pane.toolbarWidth = null;
    }
    if (refit && !collapsed) refitPanes.push(pane);
  });
  if (moving && !reducedMotion.matches) {
    // 动画期间暂缓工具栏测量与 xterm/ConPTY fit，保证两段式动画全程零变形、满帧运行。
    window.dispatchEvent(new Event("ff:layout-changed"));
    moving.element.scrollIntoView({block:"nearest", inline:"nearest"});
    animatePaneLayout(before, moving, ids, refitPanes, movingCollapsed, placeholder);
    return;
  }
  // 无动画或减弱动态效果时，所有窗格归位后立即按最终列宽收纳按钮、调整 ConPTY 尺寸。
  for (const id of ids) fitPaneToolbar(panes.get(id));
  for (const pane of refitPanes) fitPane(pane);
  if (!moving) return;
  window.dispatchEvent(new Event("ff:layout-changed"));
  moving.element.scrollIntoView({block:"nearest", inline:"nearest"});
  animatePaneLayout(before, moving, [], [], movingCollapsed, null);
}

function topicTag(pane) { return document.querySelector('.topic-agent-tag[data-agent="'+CSS.escape(pane.agent.id)+'"]'); }

function animatePaneLayout(before, moving, deferredIds = [], deferredRefits = [], collapsing = false, placeholder = null) {
  const from = before.get(moving);
  const to = (collapsing ? topicTag(moving) : moving?.element)?.getBoundingClientRect();
  if (!from || !to || !from.width || !from.height || !to.width || !to.height ||
      (from.x === to.x && from.y === to.y && from.width === to.width && from.height === to.height)) {
    placeholder?.remove();
    if (deferredIds.length || deferredRefits.length) schedulePendingFits(deferredIds, deferredRefits);
    return;
  }
  for (const id of deferredIds) pendingFitIds.add(id);
  for (const pane of deferredRefits) pendingRefitPanes.add(pane);

  // 用统一比例的矢量幽灵卡片连接起终点，绝不对真实终端做非等比拉伸。
  const card = createFlightCard(moving);
  Object.assign(card.style, {
    left: to.x + "px",
    top: to.y + "px",
    width: to.width + "px",
    height: to.height + "px"
  });
  document.body.append(card);

  const fromCx = from.x + from.width / 2, fromCy = from.y + from.height / 2;
  const toCx = to.x + to.width / 2, toCy = to.y + to.height / 2;
  const dx = fromCx - toCx, dy = fromCy - toCy;
  const uniformRatio = collapsing
    ? Math.min(2.4, Math.max(1.28, Math.min(from.width / to.width, from.height / to.height) * 0.72 + 0.56))
    : Math.max(0.28, Math.min(0.68, Math.max(from.width / to.width, from.height / to.height)));

  // 缩小：先向原窗口中心等比缩减，再飞到顶部胶囊；展开：从顶部胶囊等比放大到目标中心。
  const frames = collapsing
    ? [
        { transform: `translate(${dx}px, ${dy}px) scale(${uniformRatio})`, opacity: 0.94, offset: 0 },
        { transform: `translate(${dx}px, ${dy}px) scale(${Math.max(0.76, uniformRatio * 0.56)})`, opacity: 0.98, offset: 0.36 },
        { transform: "translate(0, 0) scale(1)", opacity: 0.18, offset: 1 }
      ]
    : [
        { transform: `translate(${dx}px, ${dy}px) scale(${uniformRatio})`, opacity: 0.96, offset: 0 },
        { transform: "translate(0, 0) scale(0.88)", opacity: 0.94, offset: 0.62 },
        { transform: "translate(0, 0) scale(1)", opacity: 0, offset: 1 }
      ];

  const duration = collapsing ? 200 : 190;
  const timing = { duration, easing: "cubic-bezier(0.22, 1, 0.36, 1)" };

  // 飞行期间先淡隐目标容器，落位后再平滑显现，避免双影或文字突变。
  moving.element.style.opacity = "0";
  layoutAnimCount++;

  if (!collapsing) {
    // 展开时先让其余窗格纯位移让出槽位，与矢量卡片飞入同步。
    animateSiblingsTranslate(before, moving);
  }

  let finished = false;
  const flight = card.animate(frames, timing);
  moving.layoutFlight = flight;

  const completeStage = (canceled = false) => {
    if (finished) return;
    finished = true;
    if (activeStageCleanup === cancelStage) activeStageCleanup = null;
    card.remove();
    if (moving.layoutFlight === flight) moving.layoutFlight = null;
    moving.element.style.opacity = "";

    if (!canceled && !reducedMotion.matches) {
      // 目标卡片/窗格落位淡入
      const reveal = (collapsing ? topicTag(moving) ?? moving.element : moving.element).animate(
        collapsing
          ? [{ opacity: 0, transform: "scale(0.92)" }, { opacity: 1, transform: "scale(1)" }]
          : [{ opacity: 0 }, { opacity: 1 }],
        { duration: 110, easing: "ease-out" }
      );
      moving.layoutMotion = reveal;
    }

    if (placeholder) {
      // 阶段 2：已飞入顶部托盘后，再移除原占位，让其余窗格纯位移平滑自适应。
      const siblingBefore = new Map();
      if (!canceled && !reducedMotion.matches) {
        for (const pane of panes.values()) {
          if (pane !== moving && !pane.element.hidden && pane.element.parentElement === placeholder.parentElement) {
            siblingBefore.set(pane, pane.element.getBoundingClientRect());
          }
        }
      }
      placeholder.remove();
      window.dispatchEvent(new Event("ff:layout-changed"));
      if (!canceled && !reducedMotion.matches) {
        animateSiblingsTranslate(siblingBefore, moving);
      }
    }

    layoutAnimCount = Math.max(0, layoutAnimCount - 1);
    if (layoutAnimCount === 0) schedulePendingFits();
  };

  const cancelStage = () => {
    flight.cancel();
    completeStage(true);
  };
  activeStageCleanup = cancelStage;
  flight.onfinish = () => completeStage(false);
  flight.oncancel = () => completeStage(true);
}

function togglePrimary(pane) {
  if (layout.workspaceMode === "code") return;
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
  if (moreMenu.childElementCount > 0) {
    for (const item of toolbarItems) {
      if (item.parentElement === moreMenu) actions.insertBefore(item, more);
    }
  }
  const gap = pane.toolbarGap ?? (pane.toolbarGap = parseFloat(getComputedStyle(actions).columnGap) || 0);
  let used = more.offsetWidth;
  const widths = toolbarItems.map(item => item.offsetWidth);
  for (let index = 0; index < widths.length; index++) {
    used += widths[index] + gap;
    if (used > available) {
      moreMenu.prepend(...toolbarItems.slice(index));
      break;
    }
  }
  if (moreMenu.contains(focused)) more.focus({preventScroll:true});
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

function applyWorkspaceMode() {
  const code = layout.workspaceMode === "code";
  const open = code || layout.agentInspectorOpen;
  const hidden = code && layout.codeAgentHidden;
  const stage = byId("workspace-stage");
  stage.dataset.mode = layout.workspaceMode;
  stage.dataset.panel = String(open);
  stage.dataset.agentsHidden = String(hidden);
  byId("agent-stage").hidden = hidden;
  byId("inspector-shell").hidden = !open;
  byId("inspector-divider").hidden = !open || hidden;
  byId("toggle-inspector").hidden = code;
  byId("toggle-inspector").setAttribute("aria-expanded", String(open));
  byId("inspector-toolbar-host").hidden = !open;
  const action = byId("inspector-panel-action");
  action.title = code ? hidden ? "展开 Agent" : "收起 Agent" : "收起面板";
  action.setAttribute("aria-label", action.title);
  action.lastElementChild.textContent = action.title;
  byId("workspace-mode-switch").querySelectorAll("button").forEach(b => b.setAttribute("aria-pressed", String(b.dataset.mode === layout.workspaceMode)));
  applyInspectorRatio();
}

function applyInspectorRatio() {
  const code = layout.workspaceMode === "code";
  const value = code ? layout.codeAgentRatio : layout.agentInspectorRatio;
  byId("workspace-stage").style.setProperty("--panel-ratio", value);
  const divider = byId("inspector-divider");
  divider.setAttribute("aria-valuenow", String(Math.round(value * 100)));
  divider.setAttribute("aria-valuemin", code ? "24" : "22");
  divider.setAttribute("aria-valuemax", code ? "50" : "45");
  divider.setAttribute("aria-valuetext", (code ? "Agent 区 " : "速览栏 ") + Math.round(value * 100) + "%");
}

function resizeInspector(position) {
  const available = byId("workspace-stage").clientWidth - byId("inspector-divider").offsetWidth;
  if (available <= 0) return;
  const code = layout.workspaceMode === "code";
  const key = code ? "codeAgentRatio" : "agentInspectorRatio";
  layout[key] = workspacePreferences({...layout, [key]:code ? position / available : 1 - position / available})[key];
  applyInspectorRatio();
}

function attachLayoutControls() {
  singleColumn.addEventListener("change", () => { if (snapshot) renderLayout(); });
  new ResizeObserver(([entry]) => {
    const narrowed = entry.contentRect.width < 650;
    if (narrowed === narrowAgentRegion || splitDragging) return;
    if (snapshot && layout.workspaceMode === "agent" && layout.agentInspectorOpen) renderLayout();
  }).observe(byId("workspace-layout"));
  window.addEventListener("resize", flushActiveStage);
  const change = (property, value) => {
    if (!layout) return;
    layout[property] = value;
    saveLayout();
    renderLayout();
    window.dispatchEvent(new Event("ff:layout-changed"));
  };
  byId("workspace-mode-switch").onclick = e => {
    const mode = e.target.closest("button")?.dataset.mode;
    if (mode) change("workspaceMode", mode);
  };
  window.addEventListener("keydown", e => {
    if (!layout || e.defaultPrevented || e.isComposing || !e.ctrlKey || !e.shiftKey || e.altKey || e.metaKey ||
        e.code !== "KeyE" || document.querySelector("dialog[open]")) return;
    e.preventDefault(); e.stopPropagation();
    change("workspaceMode", layout.workspaceMode === "agent" ? "code" : "agent");
  }, true);
  byId("toggle-inspector").onclick = () => change("agentInspectorOpen", !layout.agentInspectorOpen);
  byId("inspector-panel-action").onclick = () => layout.workspaceMode === "code"
    ? change("codeAgentHidden", !layout.codeAgentHidden) : change("agentInspectorOpen", false);
  attachDivider(byId("pane-divider"), byId("workspace-layout"), resizeSplit);
  attachDivider(byId("inspector-divider"), byId("workspace-stage"), resizeInspector);
}

function attachDivider(divider, area, resize) {
  let offset;
  let dragRaf = 0;
  let dragClientX = 0;
  let areaLeft = 0;
  divider.addEventListener("pointerdown", event => {
    if (!event.isPrimary || event.button !== 0) return;
    const dividerRect = divider.getBoundingClientRect();
    areaLeft = area.getBoundingClientRect().left;
    offset = event.clientX - dividerRect.left;
    splitDragging = true;
    inspectorDragging = divider.id === "inspector-divider";
    divider.setPointerCapture(event.pointerId);
  });
  divider.addEventListener("pointermove", event => {
    if (!divider.hasPointerCapture(event.pointerId)) return;
    dragClientX = event.clientX;
    if (dragRaf) return;
    dragRaf = requestAnimationFrame(() => {
      dragRaf = 0;
      resize(dragClientX - areaLeft - offset);
    });
  });
  divider.addEventListener("lostpointercapture", () => {
    if (dragRaf) {
      cancelAnimationFrame(dragRaf);
      dragRaf = 0;
      resize(dragClientX - areaLeft - offset);
    }
    splitDragging = false;
    inspectorDragging = false;
    if (divider.id === "inspector-divider") renderLayout();
    saveLayout();
    window.dispatchEvent(new Event("ff:layout-changed"));
    schedulePendingFits();
  });
  divider.addEventListener("keydown", event => {
    const step = { ArrowLeft: -24, ArrowRight: 24 }[event.key];
    if (!step) return;
    event.preventDefault();
    resize(divider.getBoundingClientRect().left - area.getBoundingClientRect().left + step);
    saveLayout();
  });
}

export { applySplitRatio, attachLayoutControls, fitPaneToolbar, focusAgent, isLayoutAnimating, layoutKey, narrow, readLayout, renderLayout, saveLayout, setLayoutProbe, togglePrimary };

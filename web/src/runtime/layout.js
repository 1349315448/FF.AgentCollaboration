import { byId } from "./dom.js";
import { movePane, panes } from "../services/terminalRegistry";
import { layout, selectedTopicId, snapshot } from "../store/workbenchStore";
import { fitPane } from "./terminal.js";

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

let renderLayout = layoutPanes;
function setLayoutProbe(callback) { renderLayout = callback; }

function layoutPanes(moving = null) {
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
    movePane(pane, target);
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
  for (const item of toolbarItems) {
    if (item.parentElement === moreMenu) actions.insertBefore(item, more);
  }
  const gap = parseFloat(getComputedStyle(actions).columnGap);
  const widths = toolbarItems.map(item => parseFloat(getComputedStyle(item).width));
  let used = parseFloat(getComputedStyle(more).width);
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

export { applySplitRatio, attachLayoutControls, fitPaneToolbar, focusAgent, layoutKey, narrow, readLayout, renderLayout, saveLayout, setLayoutProbe, togglePrimary };
